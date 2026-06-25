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
        if (!ShouldTriggerWorkflowTick(eventName, action, envelope?.Issue?.PullRequest is not null))
        {
            return Results.Accepted(value: new { accepted = false, eventName, action, deliveryId });
        }

        var requeuedWorkflowId = await RequeueCompletedPullRequestWorkflowAsync(envelope, eventName, cancellationToken);
        notifier.Signal();
        logger.LogInformation(
            "Accepted GitHub webhook delivery {DeliveryId} for event {EventName}/{Action}; workflow tick signaled. Requeued workflow: {WorkflowId}",
            deliveryId,
            eventName,
            action,
            requeuedWorkflowId);
        return Results.Accepted(value: new { accepted = true, eventName, action, deliveryId, requeuedWorkflowId });
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
        => eventName switch
        {
            "issues" => IsOneOf(action, "opened", "edited", "reopened", "labeled", "unlabeled"),
            "issue_comment" => IsOneOf(action, "created", "edited") || issueCommentIsPullRequest && IsOneOf(action, "deleted"),
            "pull_request" => IsOneOf(action, "opened", "reopened", "synchronize", "ready_for_review", "converted_to_draft", "closed"),
            "pull_request_review_comment" => IsOneOf(action, "created", "edited"),
            "pull_request_review" => IsOneOf(action, "submitted", "edited", "dismissed"),
            _ => false
        };

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

    private sealed record GitHubWebhookPullRequest([property: JsonPropertyName("html_url")] string? HtmlUrl);
}