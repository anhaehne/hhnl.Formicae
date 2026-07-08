using hhnl.Formicae.Application.Workflows;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Api;

public sealed class GitHubWebhookOptions
{
    public string Secret { get; set; } = string.Empty;
}

public sealed class GitHubWebhookHandler(
    WorkflowTickNotifier notifier,
    IWorkflowStore store,
    IOptions<GitHubWebhookOptions> options,
    ILogger<GitHubWebhookHandler> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IResult> HandleAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var eventName = request.Headers["X-GitHub-Event"].ToString();
        var deliveryId = request.Headers["X-GitHub-Delivery"].ToString();
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return Results.BadRequest(new { error = "Missing X-GitHub-Event header." });
        }

        using var memory = new MemoryStream();
        await request.Body.CopyToAsync(memory, cancellationToken);
        var body = memory.ToArray();

        if (!VerifySignature(body, request.Headers["X-Hub-Signature-256"].ToString(), options.Value.Secret))
        {
            logger.LogWarning("Rejected GitHub webhook delivery {DeliveryId} for event {EventName}: signature verification failed.", deliveryId, eventName);
            return Results.Unauthorized();
        }

        if (string.Equals(eventName, "ping", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new { accepted = true, eventName, deliveryId });
        }

        GitHubWebhookEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<GitHubWebhookEnvelope>(body, JsonOptions);
        }
        catch (JsonException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }

        var action = envelope?.Action ?? string.Empty;
        var issueCommentIsPullRequest = envelope?.Issue?.PullRequest is not null;
        if (!ShouldTriggerWorkflowTick(eventName, action, issueCommentIsPullRequest))
        {
            return Results.Accepted(value: new { accepted = false, eventName, action, deliveryId });
        }

        var processor = new DevOpsWebhookProcessor(store);
        var processingResult = await processor.ProcessAsync(
            new DevOpsWebhookEvent(
                "GitHub",
                eventName,
                action,
                issueCommentIsPullRequest,
                envelope?.PullRequest?.HtmlUrl,
                envelope?.PullRequest?.Merged,
                GetPullRequestUrl(envelope, eventName)),
            cancellationToken);
        notifier.Signal();
        logger.LogInformation(
            "Accepted GitHub webhook delivery {DeliveryId} for event {EventName}/{Action}; workflow tick signaled. Completed workflow: {CompletedWorkflowId}; requeued workflow: {RequeuedWorkflowId}",
            deliveryId,
            eventName,
            action,
            processingResult.CompletedWorkflowId,
            processingResult.RequeuedWorkflowId);
        return Results.Accepted(value: new { accepted = true, eventName, action, deliveryId, processingResult.CompletedWorkflowId, processingResult.RequeuedWorkflowId });
    }

    private async Task<Guid?> CompleteMergedPullRequestWorkflowAsync(
        GitHubWebhookEnvelope? envelope,
        string eventName,
        string action,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(eventName, "pull_request", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(action, "closed", StringComparison.OrdinalIgnoreCase)
            || envelope?.PullRequest?.Merged != true
            || string.IsNullOrWhiteSpace(envelope.PullRequest.HtmlUrl))
        {
            return null;
        }

        var workflow = await store.GetWorkflowByPullRequestUrlAsync(envelope.PullRequest.HtmlUrl, cancellationToken);
        if (workflow is null || workflow.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Canceled)
        {
            return null;
        }

        workflow.Status = WorkflowStatus.Completed;
        workflow.CurrentStep = WorkflowStep.Done;
        workflow.FailureReason = null;
        workflow.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpdateWorkflowAsync(workflow, cancellationToken);
        await store.AddEventAsync(new WorkflowEvent
        {
            WorkflowId = workflow.Id,
            Type = WorkflowEventTypes.WorkflowCompleted,
            Message = "Workflow completed because the pull request was merged.",
            DetailsJson = JsonSerializer.Serialize(new { pullRequestUrl = workflow.PullRequestUrl, eventName, action }),
            CreatedAt = workflow.UpdatedAt
        }, cancellationToken);
        await store.AddLogAsync(new WorkflowLog
        {
            WorkflowId = workflow.Id,
            Message = "Workflow marked completed from GitHub pull_request closed/merged webhook."
        }, cancellationToken);
        return workflow.Id;
    }
    private async Task<Guid?> RequeueCompletedPullRequestWorkflowAsync(
        GitHubWebhookEnvelope? envelope,
        string eventName,
        CancellationToken cancellationToken)
    {
        var pullRequestUrl = GetPullRequestUrl(envelope, eventName);
        if (string.IsNullOrWhiteSpace(pullRequestUrl))
        {
            return null;
        }

        var workflow = await store.GetWorkflowByPullRequestUrlAsync(pullRequestUrl, cancellationToken);
        if (workflow?.Status != WorkflowStatus.Completed)
        {
            return null;
        }

        workflow.Status = WorkflowStatus.Reviewing;
        workflow.CurrentStep = WorkflowStep.AddressComments;
        workflow.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpdateWorkflowAsync(workflow, cancellationToken);
        await store.AddLogAsync(new WorkflowLog
        {
            WorkflowId = workflow.Id,
            Message = $"Workflow requeued from GitHub {eventName} webhook for pull request comments."
        }, cancellationToken);
        return workflow.Id;
    }

    private static string? GetPullRequestUrl(GitHubWebhookEnvelope? envelope, string eventName)
    {
        if (envelope is null)
        {
            return null;
        }

        return eventName switch
        {
            "issue_comment" => envelope.Issue?.PullRequest is null ? null : envelope.Issue.HtmlUrl,
            "pull_request_review_comment" or "pull_request_review" => envelope.PullRequest?.HtmlUrl,
            _ => null
        };
    }

    public static bool VerifySignature(byte[] body, string signatureHeader, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(signatureHeader) || !signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var signature = Encoding.ASCII.GetBytes(signatureHeader);
        var expected = Encoding.ASCII.GetBytes("sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant());
        return signature.Length == expected.Length && CryptographicOperations.FixedTimeEquals(signature, expected);
    }

    public static bool ShouldTriggerWorkflowTick(string eventName, string action, bool issueCommentIsPullRequest)
        => DevOpsWebhookProcessor.ShouldTriggerWorkflowTick(eventName, action, issueCommentIsPullRequest);

    private static bool IsOneOf(string value, params string[] expectedValues)
    {
        foreach (var expected in expectedValues)
        {
            if (string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record GitHubWebhookEnvelope(
        string? Action,
        GitHubWebhookIssue? Issue,
        [property: JsonPropertyName("pull_request")] GitHubWebhookPullRequest? PullRequest);

    private sealed record GitHubWebhookIssue(
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("pull_request")] GitHubWebhookPullRequest? PullRequest);

    private sealed record GitHubWebhookPullRequest(
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("merged")] bool? Merged);
}
