using System.Security.Cryptography;
using System.Text;
using hhnl.Formicae.Api;
using hhnl.Formicae.Application.Integrations;
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
                "html_url": "https://github.com/acme/widgets/issues/23",
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

    [Fact]
    public async Task HandleAsync_Completes_workflow_for_merged_pull_request()
    {
        var store = new InMemoryWorkflowStore();
        var pullRequestUrl = "https://github.com/acme/widgets/pull/23";
        var workflow = await store.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = "https://github.com/acme/widgets/issues/2",
            RepositoryUrl = "https://github.com/acme/widgets",
            PullRequestUrl = pullRequestUrl,
            Status = WorkflowStatus.Reviewing,
            CurrentStep = WorkflowStep.AddressComments
        }, CancellationToken.None);
        var handler = new GitHubWebhookHandler(
            new WorkflowTickNotifier(),
            store,
            Options.Create(new GitHubWebhookOptions()),
            NullLogger<GitHubWebhookHandler>.Instance);
        var body = Encoding.UTF8.GetBytes($$"""
            {
              "action": "closed",
              "pull_request": {
                "html_url": "{{pullRequestUrl}}",
                "merged": true
              }
            }
            """);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-GitHub-Event"] = "pull_request";
        context.Request.Headers["X-GitHub-Delivery"] = "delivery-merge";
        context.Request.Body = new MemoryStream(body);

        await handler.HandleAsync(context.Request, CancellationToken.None);

        var completed = await store.GetWorkflowAsync(workflow.Id, CancellationToken.None);
        Assert.NotNull(completed);
        Assert.Equal(WorkflowStatus.Completed, completed.Status);
        Assert.Equal(WorkflowStep.Done, completed.CurrentStep);
        var events = await store.ListEventsAsync(workflow.Id, CancellationToken.None);
        Assert.Contains(events, evt => evt.Type == WorkflowEventTypes.WorkflowCompleted && evt.Message.Contains("merged", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HandleAsync_issues_labeled_delegates_after_signature_validation()
    {
        var store = new InMemoryWorkflowStore();
        var integrations = new InMemoryDevOpsIntegrationStore();
        var triggerService = await CreateTriggerServiceAsync(store, integrations, DevOpsProviderType.GitHub, "https://github.com/acme/widgets");
        var secret = "webhook-secret";
        var body = Encoding.UTF8.GetBytes("""
            {
              "action": "labeled",
              "repository": {
                "html_url": "https://github.com/acme/widgets",
                "full_name": "acme/widgets"
              },
              "issue": {
                "html_url": "https://github.com/acme/widgets/issues/13"
              },
              "label": {
                "name": "formicae"
              }
            }
            """);
        var handler = new GitHubWebhookHandler(
            new WorkflowTickNotifier(),
            store,
            Options.Create(new GitHubWebhookOptions { Secret = secret }),
            NullLogger<GitHubWebhookHandler>.Instance,
            triggerService);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-GitHub-Event"] = "issues";
        context.Request.Headers["X-GitHub-Delivery"] = "delivery-label";
        context.Request.Headers["X-Hub-Signature-256"] = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();
        context.Request.Body = new MemoryStream(body);

        await handler.HandleAsync(context.Request, CancellationToken.None);

        var workflow = await store.GetWorkflowByIssueUrlAsync("https://github.com/acme/widgets/issues/13", CancellationToken.None);
        Assert.NotNull(workflow);
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

    private static async Task<WorkflowTriggerService> CreateTriggerServiceAsync(
        InMemoryWorkflowStore store,
        InMemoryDevOpsIntegrationStore integrations,
        DevOpsProviderType provider,
        string repositoryUrl)
    {
        var integration = await integrations.CreateAsync(new DevOpsIntegration
        {
            ProviderType = provider,
            DisplayName = provider.ToString(),
            WebhookSecret = "webhook-secret",
            WebhookUrl = "https://formicae.example/webhook"
        }, CancellationToken.None);
        var repository = await integrations.AddRepositoryAsync(new ConnectedRepository
        {
            DevOpsIntegrationId = integration.Id,
            Owner = "acme",
            Name = "widgets",
            RepositoryUrl = repositoryUrl,
            DefaultBranch = "main"
        }, CancellationToken.None);
        var validator = new WorkflowDefinitionValidator();
        var definitions = new WorkflowDefinitionService(store, validator, integrations);
        var definition = await definitions.CreateAsync(new CreateWorkflowDefinitionRequest("Triggered workflow"), CancellationToken.None);
        await definitions.CreateVersionAsync(
            definition.Id,
            new CreateWorkflowDefinitionVersionRequest(null, true, false, new WorkflowDefinitionDocument(
                DefaultWorkflowDefinitions.V1Alpha1Schema,
                "plan",
                [new WorkflowDefinitionStep("plan", "builtins.plan")],
                [new WorkflowDefinitionTrigger("triage", WorkflowTriggerType.DevOpsIssueLabel, true, [repository.Id], "formicae")])),
            CancellationToken.None);
        var workflows = new WorkflowService(store, workflowDefinitions: definitions);
        return new WorkflowTriggerService(store, integrations, workflows);
    }
}
