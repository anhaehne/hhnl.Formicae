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
    ILogger<GiteaWebhookHandler> logger)
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
        if (!DevOpsWebhookProcessor.ShouldTriggerWorkflowTick(eventName, action, issueCommentIsPullRequest))
        {
            return Results.Accepted(value: new { accepted = false, eventName, action, deliveryId });
        }

        var processor = new DevOpsWebhookProcessor(store);
        var processingResult = await processor.ProcessAsync(
            new DevOpsWebhookEvent(
                "Gitea",
                eventName,
                action,
                issueCommentIsPullRequest,
                envelope?.PullRequest?.HtmlUrl,
                envelope?.PullRequest?.Merged,
                GetPullRequestUrl(envelope, eventName)),
            cancellationToken);
        notifier.Signal();
        return Results.Accepted(value: new { accepted = true, eventName, action, deliveryId, processingResult.CompletedWorkflowId, processingResult.RequeuedWorkflowId });
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
        [property: JsonPropertyName("pull_request")] GiteaWebhookPullRequest? PullRequest);

    private sealed record GiteaWebhookIssue(
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("pull_request")] GiteaWebhookPullRequest? PullRequest);

    private sealed record GiteaWebhookPullRequest(
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("merged")] bool? Merged);
}
