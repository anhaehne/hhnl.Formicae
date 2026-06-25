using System.Security.Cryptography;
using System.Text;
using hhnl.Formicae.Api;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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


    [Fact]
    public async Task HandleAsync_Requeues_completed_workflow_for_pull_request_issue_comment()
    {
        var store = new InMemoryWorkflowStore();
        var pullRequestUrl = "https://github.com/acme/widgets/pull/23";
        var workflow = await store.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = "https://github.com/acme/widgets/issues/2",
            RepositoryUrl = "https://github.com/acme/widgets",
            PullRequestUrl = pullRequestUrl,
            Status = WorkflowStatus.Completed,
            CurrentStep = WorkflowStep.Done
        }, CancellationToken.None);
        var handler = new GitHubWebhookHandler(
            new WorkflowTickNotifier(),
            store,
            Options.Create(new GitHubWebhookOptions()),
            NullLogger<GitHubWebhookHandler>.Instance);
        var body = Encoding.UTF8.GetBytes($$"""
            {
              "action": "created",
              "issue": {
                "html_url": "{{pullRequestUrl}}",
                "pull_request": {}
              }
            }
            """);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-GitHub-Event"] = "issue_comment";
        context.Request.Headers["X-GitHub-Delivery"] = "delivery-1";
        context.Request.Body = new MemoryStream(body);

        await handler.HandleAsync(context.Request, CancellationToken.None);

        var requeued = await store.GetWorkflowAsync(workflow.Id, CancellationToken.None);
        Assert.NotNull(requeued);
        Assert.Equal(WorkflowStatus.Reviewing, requeued.Status);
        Assert.Equal(WorkflowStep.AddressComments, requeued.CurrentStep);
        var logs = await store.ListLogsAsync(workflow.Id, CancellationToken.None);
        Assert.Contains(logs, log => log.Message.Contains("requeued", StringComparison.OrdinalIgnoreCase));
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