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

        notifier.Signal();
        logger.LogInformation("Accepted GitHub webhook delivery {DeliveryId} for event {EventName}/{Action}; workflow tick signaled.", deliveryId, eventName, action);
        return Results.Accepted(value: new { accepted = true, eventName, action, deliveryId });
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

    private sealed record GitHubWebhookEnvelope(string? Action, GitHubWebhookIssue? Issue);
    private sealed record GitHubWebhookIssue([property: JsonPropertyName("pull_request")] object? PullRequest);
}