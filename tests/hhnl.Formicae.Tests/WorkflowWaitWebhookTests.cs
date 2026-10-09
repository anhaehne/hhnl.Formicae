using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using hhnl.Formicae.Api;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowWaitWebhookTests
{
    [Fact]
    public async Task Live_HTTP_comments_resume_the_original_execution_once_and_expose_evidence()
    {
        const string repository = "https://github.com/acme/repo";
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(false).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.Configure<GitHubWebhookOptions>(options => options.Secret = "fixture")));
        factory.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var integrations = scope.ServiceProvider.GetRequiredService<IDevOpsIntegrationStore>();
        var integration = await integrations.CreateAsync(new() { ProviderType = DevOpsProviderType.GitHub }, default);
        await integrations.AddRepositoryAsync(new() { DevOpsIntegrationId = integration.Id, RepositoryUrl = repository }, default);
        var created = await client.PostAsJsonAsync("/api/workflow-definitions", new { name = "Live wait" });
        created.EnsureSuccessStatusCode();
        var definitionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var saved = await client.PostAsJsonAsync($"/api/workflow-definitions/{definitionId}/versions", new { isEnabled = true, isDefault = false,
            definition = new { schema = DefaultWorkflowDefinitions.V1Alpha3Schema, startStepId = "start", steps = new object[] {
                new { id = "start", uses = "builtins.start", nextStepId = "wait", @event = new { enabled = true } },
                new { id = "wait", uses = GitHubIssueCommentWaitDefinition.Uses, wait = new { issueNumber = 7 } } } } });
        saved.EnsureSuccessStatusCode();
        var started = await client.PostAsJsonAsync("/api/workflows/github-issue", new StartGitHubIssueWorkflowRequest(repository + "/issues/1", repository, null, null, definitionId));
        started.EnsureSuccessStatusCode();
        var workflowId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workflowId").GetGuid();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowStore>();
        var workflow = (await store.GetWorkflowAsync(workflowId, default))!;
        var orchestrator = scope.ServiceProvider.GetRequiredService<WorkflowOrchestrator>();
        await using var handle = await scope.ServiceProvider.GetRequiredService<IWorkflowOrchestrationLock>().TryAcquireAsync(default);
        Assert.NotNull(handle);
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(TaskRunStatus.Waiting, Assert.Single(await store.ListTaskRunsAsync(workflowId, default)).Status);
        for (var id = 71; id < 74; id++)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new { action = "created", repository = new { html_url = repository },
                issue = new { number = 7, html_url = repository + "/issues/7" }, comment = new { id, body = "continue", user = new { login = "reviewer" },
                    html_url = repository + "/issues/7#issuecomment-" + id, created_at = DateTimeOffset.UtcNow } });
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/github") { Content = new ByteArrayContent(body) };
            request.Headers.Add("X-GitHub-Event", "issue_comment");
            request.Headers.Add("X-GitHub-Delivery", "live-" + id);
            request.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("fixture"), body)).ToLowerInvariant());
            (await client.SendAsync(request)).EnsureSuccessStatusCode();
        }
        for (var i = 0; i < 3; i++) await orchestrator.AdvanceAsync(workflow, default);
        var execution = (await client.GetFromJsonAsync<WorkflowExecutionResponse>($"/api/workflows/{workflowId}/execution"))!;
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Single(await store.ListRecentWorkflowsAsync(10, default));
        var wait = Assert.Single(execution.Waits!);
        Assert.NotNull(wait.MatchedEventId);
        Assert.Equal(TaskRunStatus.Succeeded, Assert.Single(await store.ListTaskRunsAsync(workflowId, default)).Status);
    }

    [Theory]
    [InlineData("created", false, true, true)]
    [InlineData("created", false, false, false)]
    [InlineData("created", true, true, false)]
    [InlineData("edited", false, true, false)]
    [InlineData("deleted", false, true, false)]
    public async Task Only_signed_new_issue_comments_can_satisfy_waits(string action, bool pullRequest, bool signed, bool expected)
    {
        const string repo = "https://github.com/acme/repo";
        var store = new InMemoryWorkflowStore();
        var workflow = await store.CreateWorkflowAsync(new() { IssueUrl = repo + "/issues/7", RepositoryUrl = repo, Status = WorkflowStatus.Running }, default);
        var run = await store.UpsertTaskRunAsync(new() { WorkflowId = workflow.Id, Kind = TaskRunKind.Wait, Status = TaskRunStatus.Waiting, ExecutionAttemptId = Guid.NewGuid() }, default);
        var wait = await store.ArmWaitAsync(new() { WorkflowId = workflow.Id, TaskRunId = run.Id, ExecutionAttemptId = run.ExecutionAttemptId!.Value,
            Uses = GitHubIssueCommentWaitDefinition.Uses, Provider = "GitHub", RepositoryUrl = repo, IssueUrl = workflow.IssueUrl, ArmedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, default);
        var handler = new GitHubWebhookHandler(new(), store, Options.Create(new GitHubWebhookOptions { Secret = "fixture" }), NullLogger<GitHubWebhookHandler>.Instance);
        var body = JsonSerializer.SerializeToUtf8Bytes(new { action, repository = new { html_url = repo },
            issue = new { number = 7, html_url = workflow.IssueUrl, pull_request = pullRequest ? new { } : null },
            comment = new { id = 71, body = "new comment", user = new { login = "bot" }, html_url = workflow.IssueUrl + "#issuecomment-71", created_at = DateTimeOffset.UtcNow } });
        var context = new DefaultHttpContext();
        context.Request.Headers["X-GitHub-Event"] = "issue_comment";
        context.Request.Headers["X-GitHub-Delivery"] = "signed-delivery";
        context.Request.Headers["X-Hub-Signature-256"] = signed ? "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("fixture"), body)).ToLowerInvariant() : "sha256=invalid";
        context.Request.Body = new MemoryStream(body);
        await handler.HandleAsync(context.Request, default);
        var claimed = await store.ClaimWaitEventAsync(wait.Id, default);
        Assert.Equal(expected, claimed is not null);
        if (claimed is not null)
        {
            Assert.Equal("bot", JsonDocument.Parse(claimed.OutputsJson).RootElement.GetProperty("author").GetString());
            context.Request.Body = new MemoryStream(body);
            await handler.HandleAsync(context.Request, default);
            Assert.Equal(claimed.Id, (await store.ClaimWaitEventAsync(wait.Id, default))!.Id);
        }
        Assert.Equal(WorkflowStatus.Running, workflow.Status); // Webhooks never launch or advance a continuation.
    }
}
