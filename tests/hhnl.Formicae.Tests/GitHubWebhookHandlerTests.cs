using System.Security.Cryptography;
using System.Text;
using hhnl.Formicae.Api;

namespace hhnl.Formicae.Tests;

public sealed class GitHubWebhookHandlerTests
{
    [Fact]
    public void VerifySignature_accepts_valid_sha256_signature()
    {
        var body = Encoding.UTF8.GetBytes("{\"action\":\"labeled\"}");
        var secret = "webhook-secret";
        var signature = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

        Assert.True(GitHubWebhookHandler.VerifySignature(body, signature, secret));
    }

    [Fact]
    public void VerifySignature_rejects_invalid_signature_when_secret_is_configured()
    {
        var body = Encoding.UTF8.GetBytes("{\"action\":\"labeled\"}");

        Assert.False(GitHubWebhookHandler.VerifySignature(body, "sha256=bad", "webhook-secret"));
        Assert.False(GitHubWebhookHandler.VerifySignature(body, string.Empty, "webhook-secret"));
    }

    [Theory]
    [InlineData("issues", "labeled", false, true)]
    [InlineData("issues", "closed", false, false)]
    [InlineData("issue_comment", "created", false, true)]
    [InlineData("pull_request", "synchronize", false, true)]
    [InlineData("pull_request_review_comment", "created", false, true)]
    [InlineData("pull_request_review", "submitted", false, true)]
    [InlineData("push", "", false, false)]
    public void ShouldTriggerWorkflowTick_filters_supported_events(string eventName, string action, bool issueCommentIsPullRequest, bool expected)
    {
        Assert.Equal(expected, GitHubWebhookHandler.ShouldTriggerWorkflowTick(eventName, action, issueCommentIsPullRequest));
    }
}