using System.Security.Cryptography;
using System.Text;
using hhnl.Formicae.Api;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace hhnl.Formicae.Tests;

public sealed class GiteaWebhookHandlerTests
{
    [Fact]
    public void VerifySignature_accepts_valid_gitea_signature()
    {
        var body = Encoding.UTF8.GetBytes("{\"action\":\"labeled\"}");
        var secret = "webhook-secret";
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

        Assert.True(GiteaWebhookHandler.VerifySignature(body, signature, secret));
        Assert.True(GiteaWebhookHandler.VerifySignature(body, "sha256=" + signature, secret));
    }

    [Fact]
    public void VerifySignature_rejects_invalid_gitea_signature()
    {
        var body = Encoding.UTF8.GetBytes("{\"action\":\"labeled\"}");

        Assert.False(GiteaWebhookHandler.VerifySignature(body, "bad", "webhook-secret"));
        Assert.False(GiteaWebhookHandler.VerifySignature(body, string.Empty, "webhook-secret"));
    }

    [Fact]
    public async Task HandleAsync_accepts_ping_with_valid_signature()
    {
        var store = new InMemoryWorkflowStore();
        var integrations = await CreateIntegrationStoreAsync();
        var body = Encoding.UTF8.GetBytes("{}");
        var context = CreateRequest("ping", body, "webhook-secret");

        var result = await new GiteaWebhookHandler(new WorkflowTickNotifier(), integrations, store, NullLogger<GiteaWebhookHandler>.Instance)
            .HandleAsync(context.Request, CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task HandleAsync_Completes_workflow_for_merged_pull_request()
    {
        var store = new InMemoryWorkflowStore();
        var integrations = await CreateIntegrationStoreAsync();
        var pullRequestUrl = "https://gitea.example/acme/widgets/pulls/23";
        var workflow = await store.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = "https://gitea.example/acme/widgets/issues/2",
            RepositoryUrl = "https://gitea.example/acme/widgets",
            PullRequestUrl = pullRequestUrl,
            Status = WorkflowStatus.Reviewing,
            CurrentStep = WorkflowStep.AddressComments
        }, CancellationToken.None);
        var body = Encoding.UTF8.GetBytes($$"""
            {
              "action": "closed",
              "pull_request": {
                "html_url": "{{pullRequestUrl}}",
                "merged": true
              }
            }
            """);
        var context = CreateRequest("pull_request", body, "webhook-secret");

        await new GiteaWebhookHandler(new WorkflowTickNotifier(), integrations, store, NullLogger<GiteaWebhookHandler>.Instance)
            .HandleAsync(context.Request, CancellationToken.None);

        var completed = await store.GetWorkflowAsync(workflow.Id, CancellationToken.None);
        Assert.NotNull(completed);
        Assert.Equal(WorkflowStatus.Completed, completed.Status);
        Assert.Equal(WorkflowStep.Done, completed.CurrentStep);
    }

    [Fact]
    public async Task HandleAsync_Requeues_completed_workflow_for_pull_request_issue_comment()
    {
        var store = new InMemoryWorkflowStore();
        var integrations = await CreateIntegrationStoreAsync();
        var pullRequestUrl = "https://gitea.example/acme/widgets/pulls/23";
        var workflow = await store.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = "https://gitea.example/acme/widgets/issues/2",
            RepositoryUrl = "https://gitea.example/acme/widgets",
            PullRequestUrl = pullRequestUrl,
            Status = WorkflowStatus.Completed,
            CurrentStep = WorkflowStep.Done
        }, CancellationToken.None);
        var body = Encoding.UTF8.GetBytes($$"""
            {
              "action": "created",
              "issue": {
                "html_url": "{{pullRequestUrl}}",
                "pull_request": {}
              }
            }
            """);
        var context = CreateRequest("issue_comment", body, "webhook-secret");

        await new GiteaWebhookHandler(new WorkflowTickNotifier(), integrations, store, NullLogger<GiteaWebhookHandler>.Instance)
            .HandleAsync(context.Request, CancellationToken.None);

        var requeued = await store.GetWorkflowAsync(workflow.Id, CancellationToken.None);
        Assert.NotNull(requeued);
        Assert.Equal(WorkflowStatus.Reviewing, requeued.Status);
        Assert.Equal(WorkflowStep.AddressComments, requeued.CurrentStep);
    }

    [Fact]
    public async Task HandleAsync_issues_labeled_delegates_after_signature_validation()
    {
        var store = new InMemoryWorkflowStore();
        var integrations = await CreateIntegrationStoreAsync();
        var triggerService = await CreateTriggerServiceAsync(store, integrations, "https://gitea.example/acme/widgets");
        var body = Encoding.UTF8.GetBytes("""
            {
              "action": "labeled",
              "repository": {
                "html_url": "https://gitea.example/acme/widgets",
                "full_name": "acme/widgets"
              },
              "issue": {
                "html_url": "https://gitea.example/acme/widgets/issues/13"
              },
              "label": {
                "name": "formicae"
              }
            }
            """);
        var context = CreateRequest("issues", body, "webhook-secret");

        await new GiteaWebhookHandler(new WorkflowTickNotifier(), integrations, store, NullLogger<GiteaWebhookHandler>.Instance, triggerService)
            .HandleAsync(context.Request, CancellationToken.None);

        var workflow = await store.GetWorkflowByIssueUrlAsync("https://gitea.example/acme/widgets/issues/13", CancellationToken.None);
        Assert.NotNull(workflow);
    }

    private static DefaultHttpContext CreateRequest(string eventName, byte[] body, string secret)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Gitea-Event"] = eventName;
        context.Request.Headers["X-Gitea-Delivery"] = "delivery-1";
        context.Request.Headers["X-Gitea-Signature"] = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();
        context.Request.Body = new MemoryStream(body);
        return context;
    }

    private static async Task<InMemoryDevOpsIntegrationStore> CreateIntegrationStoreAsync()
    {
        var integrations = new InMemoryDevOpsIntegrationStore();
        await integrations.CreateAsync(new DevOpsIntegration
        {
            ProviderType = DevOpsProviderType.Gitea,
            DisplayName = "Gitea",
            ServerUrl = "https://gitea.example",
            AccessToken = "token",
            WebhookSecret = "webhook-secret",
            WebhookUrl = "https://formicae.example/api/webhooks/gitea",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        return integrations;
    }

    private static async Task<WorkflowTriggerService> CreateTriggerServiceAsync(
        InMemoryWorkflowStore store,
        InMemoryDevOpsIntegrationStore integrations,
        string repositoryUrl)
    {
        var integration = (await integrations.ListAsync(CancellationToken.None)).Single();
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
