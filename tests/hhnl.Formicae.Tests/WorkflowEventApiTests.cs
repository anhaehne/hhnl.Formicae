using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using hhnl.Formicae.Api;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowEventApiTests
{
    [Theory]
    [InlineData("opened", "created")]
    [InlineData("labeled", "label")]
    public async Task Signed_github_delivery_starts_only_the_matching_registered_event(string action, string eventId)
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(false);
        var client = factory.CreateClient();
        factory.Services.GetRequiredService<IOptions<GitHubWebhookOptions>>().Value.Secret = "fixture-signing-secret";
        using var scope = factory.Services.CreateScope();
        var integrations = scope.ServiceProvider.GetRequiredService<IDevOpsIntegrationStore>();
        var integration = await integrations.CreateAsync(new() { ProviderType = DevOpsProviderType.GitHub, DisplayName = "GitHub" }, default);
        var repo = await integrations.AddRepositoryAsync(new() { DevOpsIntegrationId = integration.Id, Owner = "acme", Name = "repo", RepositoryUrl = "https://github.com/acme/repo" }, default);
        var definitions = scope.ServiceProvider.GetRequiredService<WorkflowDefinitionService>();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowStore>();
        var definition = await definitions.CreateAsync(new("Signed events"), default);
        var version = await definitions.CreateVersionAsync(definition.Id, new(null, true, false,
            new(DefaultWorkflowDefinitions.V1Alpha3Schema, "", [
                new("created", "github.issue-created", "created-plan", Event: WorkflowEventDefinitions.Configuration(new IssueEventSettings(true, [repo.Id]))),
                new("label", "github.label-added", "label-plan", Event: WorkflowEventDefinitions.Configuration(new IssueEventSettings(true, [repo.Id], "ready"))),
                new("created-plan", "builtins.plan"), new("label-plan", "builtins.plan")])), default);
        var body = JsonSerializer.Serialize(new { action, repository = new { html_url = repo.RepositoryUrl, full_name = "acme/repo" },
            issue = new { html_url = repo.RepositoryUrl + "/issues/1", number = 1, title = "A full issue", body = "Text\nwith unicode ✓", user = new { login = "author" }, labels = new[] { new { name = "ready" } }, future_field = new { value = 7 } }, label = action == "labeled" ? new { name = "ready" } : null });
        async Task<HttpResponseMessage> Deliver(string signature)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/github") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-GitHub-Event", "issues"); request.Headers.Add("X-GitHub-Delivery", "signed-" + action);
            request.Headers.Add("X-Hub-Signature-256", signature);
            return await client.SendAsync(request);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Deliver("sha256=wrong")).StatusCode);
        Assert.Empty(await store.ListRecentWorkflowsAsync(10, default));
        var signature = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("fixture-signing-secret"), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var accepted = await Deliver(signature); Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var response = (await accepted.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal(1, response.GetProperty("startedWorkflowCount").GetInt32());
        var workflow = Assert.Single(await store.ListRecentWorkflowsAsync(10, default));
        Assert.Equal(version.Id, workflow.WorkflowDefinitionVersionId);
        Assert.Equal(eventId, Assert.Single(await store.ListTriggerEventsAsync(workflow.Id, default)).TriggerId);
        Assert.Contains($"\"eventNodeId\":\"{eventId}\"", (await store.ListEventsAsync(workflow.Id, default)).First(item => item.Type == WorkflowEventTypes.WorkflowQueued).DetailsJson!);
        var runs = await store.ListTaskRunsAsync(workflow.Id, default);
        if (action == "opened")
        {
            var evidence = Assert.Single(runs, run => run.Kind == TaskRunKind.Event);
            Assert.Equal(eventId, evidence.DefinitionStepId);
            Assert.Equal(TaskRunStatus.Succeeded, evidence.Status);
            Assert.False(workflow.IsPaused);
            using var outputs = JsonDocument.Parse(evidence.StructuredOutputsJson!);
            Assert.Equal(1, outputs.RootElement.GetProperty("issueId").GetInt32());
            Assert.Equal(JsonValueKind.String, outputs.RootElement.GetProperty("issue").ValueKind);
            using var captured = JsonDocument.Parse(outputs.RootElement.GetProperty("issue").GetString()!);
            using var original = JsonDocument.Parse(body);
            Assert.Equal(original.RootElement.GetProperty("issue").GetRawText(), captured.RootElement.GetRawText());
        }
        else Assert.DoesNotContain(runs, run => run.Kind == TaskRunKind.Event);
        var replay = (await (await Deliver(signature)).Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal(0, replay.GetProperty("startedWorkflowCount").GetInt32());
        Assert.Single(await store.ListRecentWorkflowsAsync(10, default));
        var catalog = (await client.GetFromJsonAsync<WorkflowEventDescriptor[]>("/api/workflow-events"))!;
        Assert.Contains(catalog, item => item.Uses == "github.issue-created" && item.Fields.All(field => field.Name != "label"));
        Assert.Contains(catalog, item => item.Uses == "github.label-added" && item.Fields.Any(field => field.Name == "label"));
    }
}
