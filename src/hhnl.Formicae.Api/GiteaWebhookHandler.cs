using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Api;

public sealed class GiteaWebhookHandler(
    WorkflowTickNotifier notifier,
    IDevOpsIntegrationStore integrations,
    IWorkflowStore store,
    ILogger<GiteaWebhookHandler> logger,
    WorkflowTriggerService? triggerService = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IResult> HandleAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var eventName = request.Headers["X-Gitea-Event"].ToString();
        var deliveryId = request.Headers["X-Gitea-Delivery"].ToString();
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return Results.BadRequest(new { error = "Missing X-Gitea-Event header." });
        }

        using var memory = new MemoryStream();
        await request.Body.CopyToAsync(memory, cancellationToken);
        var body = memory.ToArray();
        if (!await VerifyAnyGiteaSignatureAsync(
            body,
            request.Headers["X-Gitea-Signature"].ToString(),
            request.Headers["X-Hub-Signature-256"].ToString(),
            cancellationToken))
        {
            logger.LogWarning("Rejected Gitea webhook delivery {DeliveryId} for event {EventName}: signature verification failed.", deliveryId, eventName);
            return Results.Unauthorized();
        }

        if (string.Equals(eventName, "ping", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new { accepted = true, eventName, deliveryId });
        }

        GiteaWebhookEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<GiteaWebhookEnvelope>(body, JsonOptions);
        }
        catch (JsonException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }

        var action = envelope?.Action ?? string.Empty;
        var issueCommentIsPullRequest = envelope?.Issue?.PullRequest is not null;
        var startedWorkflowIds = triggerService is null
            ? []
            : await HandleIssueLabelTriggerAsync(triggerService, eventName, deliveryId, envelope, cancellationToken);
        var shouldTriggerWorkflowTick = DevOpsWebhookProcessor.ShouldTriggerWorkflowTick(eventName, action, issueCommentIsPullRequest);
        if (!shouldTriggerWorkflowTick && startedWorkflowIds.Count == 0)
        {
            return Results.Accepted(value: new { accepted = false, eventName, action, deliveryId, startedWorkflowIds, startedWorkflowCount = 0 });
        }

        DevOpsWebhookProcessingResult? processingResult = null;
        if (shouldTriggerWorkflowTick)
        {
            var processor = new DevOpsWebhookProcessor(store);
            processingResult = await processor.ProcessAsync(
                new DevOpsWebhookEvent(
                    "Gitea",
                    eventName,
                    action,
                    issueCommentIsPullRequest,
                    envelope?.PullRequest?.HtmlUrl,
                    envelope?.PullRequest?.Merged,
                    GetPullRequestUrl(envelope, eventName)),
                cancellationToken);
        }

        if (startedWorkflowIds.Count > 0 || processingResult?.CompletedWorkflowId is not null || processingResult?.RequeuedWorkflowId is not null)
        {
            notifier.Signal();
        }

        return Results.Accepted(value: new
        {
            accepted = true,
            eventName,
            action,
            deliveryId,
            startedWorkflowIds,
            startedWorkflowCount = startedWorkflowIds.Count,
            processingResult?.CompletedWorkflowId,
            processingResult?.RequeuedWorkflowId
        });
    }

    private static async Task<IReadOnlyList<Guid>> HandleIssueLabelTriggerAsync(
        WorkflowTriggerService triggerService,
        string eventName,
        string deliveryId,
        GiteaWebhookEnvelope? envelope,
        CancellationToken cancellationToken)
    {
        var action = envelope?.Action;
        var repositoryUrl = envelope?.Repository?.HtmlUrl ?? envelope?.Repository?.CloneUrl;
        var issueUrl = envelope?.Issue?.HtmlUrl;
        var label = envelope?.Label?.Name;
        if (!string.Equals(eventName, "issues", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(action, "labeled", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(repositoryUrl)
            || string.IsNullOrWhiteSpace(issueUrl)
            || string.IsNullOrWhiteSpace(label))
        {
            return [];
        }

        return await triggerService.HandleIssueLabelEventAsync(new DevOpsIssueLabelTriggerEvent(
            DevOpsProviderType.Gitea,
            deliveryId,
            eventName,
            action!,
            repositoryUrl!,
            issueUrl!,
            label!,
            envelope?.Repository?.FullName), cancellationToken);
    }

    public static bool VerifySignature(byte[] body, string signatureHeader, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        var expectedHex = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();
        var normalized = signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? signatureHeader["sha256=".Length..]
            : signatureHeader;
        var signature = Encoding.ASCII.GetBytes(normalized.ToLowerInvariant());
        var expected = Encoding.ASCII.GetBytes(expectedHex);
        return signature.Length == expected.Length && CryptographicOperations.FixedTimeEquals(signature, expected);
    }

    private async Task<bool> VerifyAnyGiteaSignatureAsync(byte[] body, string giteaSignature, string hubSignature, CancellationToken cancellationToken)
    {
        var configured = (await integrations.ListAsync(cancellationToken))
            .Where(integration => integration.ProviderType == DevOpsProviderType.Gitea)
            .Select(integration => integration.WebhookSecret)
            .Where(secret => !string.IsNullOrWhiteSpace(secret))
            .ToArray();
        if (configured.Length == 0)
        {
            return true;
        }

        return configured.Any(secret =>
            VerifySignature(body, giteaSignature, secret)
            || GitHubWebhookHandler.VerifySignature(body, hubSignature, secret));
    }

    private static string? GetPullRequestUrl(GiteaWebhookEnvelope? envelope, string eventName)
        => eventName switch
        {
            "issue_comment" => envelope?.Issue?.PullRequest is null ? null : envelope.Issue.HtmlUrl,
            "pull_request_review_comment" or "pull_request_review" => envelope?.PullRequest?.HtmlUrl,
            _ => null
        };

    private sealed record GiteaWebhookEnvelope(
        string? Action,
        GiteaWebhookIssue? Issue,
        GiteaWebhookRepository? Repository,
        GiteaWebhookLabel? Label,
        [property: JsonPropertyName("pull_request")] GiteaWebhookPullRequest? PullRequest);

    private sealed record GiteaWebhookIssue(
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("pull_request")] GiteaWebhookPullRequest? PullRequest);

    private sealed record GiteaWebhookPullRequest(
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("merged")] bool? Merged);

    private sealed record GiteaWebhookRepository(
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("clone_url")] string? CloneUrl,
        [property: JsonPropertyName("full_name")] string? FullName);

    private sealed record GiteaWebhookLabel(string? Name);
}
