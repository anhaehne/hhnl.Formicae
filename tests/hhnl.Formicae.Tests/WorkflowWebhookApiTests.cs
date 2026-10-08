using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using hhnl.Formicae.Application.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowWebhookApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Webhook_authenticates_pins_entry_and_deduplicates_without_retaining_secret(bool registeredEvent)
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(false);
        var client = factory.CreateClient();
        factory.Services.GetRequiredService<IConfiguration>()["WorkflowWebhooks:Secrets:fixture"] = "test-shared-secret";
        using var scope = factory.Services.CreateScope();
        var definitions = scope.ServiceProvider.GetRequiredService<WorkflowDefinitionService>();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowStore>();
        var definition = await definitions.CreateAsync(new("Webhook only"), default);
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "", [
            registeredEvent
                ? new("hook", "builtins.webhook", "event-plan", Event: WorkflowEventDefinitions.Configuration(new WebhookEventSettings(true, "fixture", "develop", "hook-model")))
                : new("hook", WorkflowStartDefinitions.Uses, "event-plan", Trigger: new(WorkflowTriggerType.Webhook, true, [], null, "develop", "hook-model", "fixture")),
            new("event-plan", "builtins.plan")
        ]);
        var version = await definitions.CreateVersionAsync(definition.Id, new(null, true, false, document), default);
        var path = $"/api/webhooks/workflows/{version.Id}/hook";
        var payload = new StartGitHubIssueWorkflowRequest("https://example.test/issues/1", "https://example.test/repo", null, null);
        client.DefaultRequestHeaders.Add("X-Formicae-Delivery", "delivery-1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path, payload)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path, payload)).StatusCode);
        Assert.Empty(await store.ListRecentWorkflowsAsync(10, default));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-shared-secret");
        var response = await client.PostAsJsonAsync(path, payload);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var started = (await response.Content.ReadFromJsonAsync<WorkflowSummaryResponse>())!;
        var workflow = (await store.GetWorkflowAsync(started.WorkflowId, default))!;
        Assert.Equal("event-plan", workflow.CurrentDefinitionStepId);
        Assert.Equal(version.Id, workflow.WorkflowDefinitionVersionId);
        Assert.Equal("develop", workflow.BaseBranch); Assert.Equal("hook-model", workflow.Model);
        var audit = Assert.Single(await store.ListTriggerEventsAsync(workflow.Id, default));
        Assert.Equal("hook", audit.TriggerId); Assert.Equal(WorkflowTriggerType.Webhook, audit.TriggerType);
        Assert.DoesNotContain("test-shared-secret", audit.PayloadSummaryJson);
        Assert.DoesNotContain("test-shared-secret", Assert.Single(await store.ListEventsAsync(workflow.Id, default)).DetailsJson!);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(path, payload with { IssueUrl = "https://example.test/issues/2" })).StatusCode);
        Assert.Single(await store.ListRecentWorkflowsAsync(10, default));
        client.DefaultRequestHeaders.Remove("X-Formicae-Delivery");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, payload)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Formicae-Delivery", "delivery-2");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, payload)).StatusCode);
        var manual = await client.PostAsJsonAsync("/api/workflows/github-issue", payload with { WorkflowDefinitionId = definition.Id, WorkflowDefinitionVersionId = version.Id });
        Assert.Equal(HttpStatusCode.BadRequest, manual.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Webhook_rejects_oversized_bodies_before_creating_an_execution(bool chunked)
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(false); var client = factory.CreateClient();
        factory.Services.GetRequiredService<IConfiguration>()["WorkflowWebhooks:Secrets:fixture"] = "test-shared-secret";
        using var scope = factory.Services.CreateScope(); var definitions = scope.ServiceProvider.GetRequiredService<WorkflowDefinitionService>();
        var definition = await definitions.CreateAsync(new("Bounded webhook"), default);
        var version = await definitions.CreateVersionAsync(definition.Id, new(null, true, false,
            new(DefaultWorkflowDefinitions.V1Alpha3Schema, "", [
                new("hook", WorkflowStartDefinitions.Uses, "plan", Trigger: new(WorkflowTriggerType.Webhook, true, [], null, WebhookSecretName: "fixture")),
                new("plan", "builtins.plan")])), default);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/workflows/{version.Id}/hook")
        {
            Content = JsonContent.Create(new StartGitHubIssueWorkflowRequest(new string('x', 70000), "https://example.test/repo", null, null))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-shared-secret");
        request.Headers.Add("X-Formicae-Delivery", "large-delivery");
        if (chunked) request.Headers.TransferEncodingChunked = true;
        else await request.Content.LoadIntoBufferAsync();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(request)).StatusCode);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IWorkflowStore>().ListRecentWorkflowsAsync(10, default));
    }

    [Theory]
    [InlineData("disabled-version")]
    [InlineData("disabled-node")]
    [InlineData("unconfigured-secret")]
    [InlineData("provider-node")]
    public async Task Webhook_requires_enabled_webhook_node_and_configured_secret(string scenario)
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(false); var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope(); var definitions = scope.ServiceProvider.GetRequiredService<WorkflowDefinitionService>();
        var definition = await definitions.CreateAsync(new("Guarded hook"), default);
        var settings = new WorkflowTriggerNodeSettings(scenario == "provider-node" ? WorkflowTriggerType.DevOpsIssueLabel : WorkflowTriggerType.Webhook,
            scenario is not ("disabled-node" or "provider-node"), [], null, WebhookSecretName: "fixture");
        var version = await definitions.CreateVersionAsync(definition.Id, new(null, scenario != "disabled-version", false,
            new(DefaultWorkflowDefinitions.V1Alpha3Schema, "", [new("hook", WorkflowStartDefinitions.Uses, "plan", Trigger: settings), new("plan", "builtins.plan")])), default);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-shared-secret");
        var response = await client.PostAsJsonAsync($"/api/webhooks/workflows/{version.Id}/hook", new StartGitHubIssueWorkflowRequest("https://example.test/issues/1", "https://example.test/repo", null, null));
        Assert.Equal(scenario == "unconfigured-secret" ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IWorkflowStore>().ListRecentWorkflowsAsync(10, default));
    }
}
