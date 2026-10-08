using System.Text.Json;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowEventNodeTests
{
    [Theory]
    [InlineData("github.issue-created", "opened", DevOpsProviderType.GitHub, true)]
    [InlineData("github.issue-created", "labeled", DevOpsProviderType.GitHub, false)]
    [InlineData("github.issue-created", "opened", DevOpsProviderType.Gitea, false)]
    [InlineData("github.label-added", "labeled", DevOpsProviderType.GitHub, true)]
    [InlineData("github.label-added", "opened", DevOpsProviderType.GitHub, false)]
    [InlineData("github.label-added", "labeled", DevOpsProviderType.Gitea, false)]
    [InlineData("gitea.label-added", "labeled", DevOpsProviderType.Gitea, true)]
    public async Task Registered_issue_events_match_only_their_provider_and_action(string uses, string action, DevOpsProviderType provider, bool expected)
    {
        var store = new InMemoryWorkflowStore(); var integrations = new InMemoryDevOpsIntegrationStore();
        var integration = await integrations.CreateAsync(new() { ProviderType = uses.StartsWith("gitea") ? DevOpsProviderType.Gitea : DevOpsProviderType.GitHub, DisplayName = "Provider" }, default);
        var repo = await integrations.AddRepositoryAsync(new() { DevOpsIntegrationId = integration.Id, Owner = "acme", Name = "repo", RepositoryUrl = "https://example.test/repo", DefaultBranch = "main" }, default);
        var definitions = new WorkflowDefinitionService(store, new(), integrations);
        var definition = await definitions.CreateAsync(new("Events"), default);
        var settings = new IssueEventSettings(true, [repo.Id], uses.EndsWith("label-added") ? "ready" : null, "develop", "event-model");
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "", [
            new("event", uses, "plan", Event: WorkflowEventDefinitions.Configuration(settings)), new("plan", "builtins.plan")]);
        var version = await definitions.CreateVersionAsync(definition.Id, new(null, true, false, document), default);
        var events = new WorkflowEventService(store, integrations, new(store, workflowDefinitions: definitions));
        var delivery = new DevOpsIssueLabelTriggerEvent(provider, "delivery", "issues", action, repo.RepositoryUrl, "https://example.test/issues/1", "ready");
        var started = await events.HandleIssueEventAsync(delivery, default);
        Assert.Equal(expected ? 1 : 0, started.Count);
        if (!expected) return;
        var workflow = (await store.GetWorkflowAsync(started[0], default))!;
        Assert.Equal(version.Id, workflow.WorkflowDefinitionVersionId); Assert.Equal("plan", workflow.CurrentDefinitionStepId);
        Assert.Equal("develop", workflow.BaseBranch); Assert.Equal("event-model", workflow.Model);
        Assert.Equal(action, Assert.Single(await store.ListTriggerEventsAsync(workflow.Id, default)).Action);
        Assert.Contains("\"eventNodeId\":\"event\"", Assert.Single(await store.ListEventsAsync(workflow.Id, default)).DetailsJson!);
        Assert.Empty(await events.HandleIssueEventAsync(delivery with { IssueUrl = "https://example.test/issues/2" }, default));
    }

    [Theory]
    [InlineData("github.issue-created", "{\"enabled\":true,\"repositoryIds\":[]}")]
    [InlineData("github.label-added", "{\"enabled\":true,\"repositoryIds\":[\"11111111-1111-1111-1111-111111111111\"]}")]
    [InlineData("builtins.webhook", "{\"enabled\":true}")]
    [InlineData("unknown.event", "{}")]
    [InlineData("github.issue-created", "null")]
    public void Invalid_event_configuration_is_rejected(string uses, string json)
    {
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "", [
            new("event", uses, "plan", Event: JsonDocument.Parse(json).RootElement.Clone()), new("plan", "builtins.plan")]);
        Assert.False(new WorkflowDefinitionValidator().Validate(document).IsValid);
    }

    [Theory]
    [InlineData("github.issue-created", DevOpsProviderType.Gitea)]
    [InlineData("github.label-added", DevOpsProviderType.Gitea)]
    [InlineData("gitea.label-added", DevOpsProviderType.GitHub)]
    public async Task Event_configuration_rejects_repositories_from_a_different_provider(string uses, DevOpsProviderType provider)
    {
        var integrations = new InMemoryDevOpsIntegrationStore();
        var integration = await integrations.CreateAsync(new() { ProviderType = provider, DisplayName = "Other provider" }, default);
        var repository = await integrations.AddRepositoryAsync(new() { DevOpsIntegrationId = integration.Id, RepositoryUrl = "https://example.test/repo" }, default);
        var definitions = new WorkflowDefinitionService(new InMemoryWorkflowStore(), new(), integrations);
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "", [
            new("event", uses, "plan", Event: WorkflowEventDefinitions.Configuration(new IssueEventSettings(true, [repository.Id], uses.EndsWith("label-added") ? "ready" : null))), new("plan", "builtins.plan")]);
        var result = await definitions.ValidateAsync(document, default);
        Assert.Contains(result.Errors, error => error.Code == "definition.event.repository.provider" && error.NodeId == "event");
    }

    [Fact]
    public async Task Built_in_upgrade_creates_one_new_version_preserving_old_definition_and_task_order()
    {
        var store = new InMemoryWorkflowStore(); var (definition, version) = DefaultWorkflowDefinitions.CreateMvp();
        version = new() { Id = version.Id, WorkflowDefinitionId = definition.Id, Version = 1, DslSchemaVersion = DefaultWorkflowDefinitions.V1Alpha1Schema,
            IsEnabled = true, IsDefault = true, DefinitionJson = WorkflowDefinitionJson.Serialize(DefaultWorkflowDefinitions.CreateMvpDocument()) };
        var oldJson = version.DefinitionJson;
        await store.EnsureDefaultWorkflowDefinitionAsync(definition, version, default);
        var service = new WorkflowDefinitionService(store, new());
        await service.UpgradeBuiltInWorkflowEventsAsync(default); await service.UpgradeBuiltInWorkflowEventsAsync(default);
        var versions = await store.ListWorkflowDefinitionVersionsAsync(definition.Id, default);
        Assert.Equal(2, versions.Count);
        Assert.Equal(oldJson, versions.Single(item => item.Id == version.Id).DefinitionJson);
        var upgraded = WorkflowDefinitionJson.Deserialize(versions.Single(item => item.Version == 2).DefinitionJson)!;
        Assert.Equal("manual-start", upgraded.StartStepId); Assert.NotNull(upgraded.Steps[0].Event);
        Assert.Equal(DefaultWorkflowDefinitions.CreateMvpDocument().Steps.Select(step => step.Id), upgraded.Steps.Skip(1).Select(step => step.Id));
        Assert.True(new WorkflowDefinitionValidator().Validate(upgraded).IsValid);
    }

    [Fact]
    public async Task Integration_can_register_and_dispatch_an_event_without_modifying_the_core_selector()
    {
        var store = new InMemoryWorkflowStore(); var integrations = new InMemoryDevOpsIntegrationStore();
        var integration = await integrations.CreateAsync(new() { ProviderType = DevOpsProviderType.GitHub, DisplayName = "Custom integration" }, default);
        var repository = await integrations.AddRepositoryAsync(new() { DevOpsIntegrationId = integration.Id, RepositoryUrl = "https://example.test/custom" }, default);
        var uses = "test.custom-event-" + Guid.NewGuid();
        WorkflowEventRegistry.Default.Register(new TestEvent(uses));
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "", [
            new("event", uses, "plan", Event: WorkflowEventDefinitions.Configuration(new IssueEventSettings(true, [repository.Id]))), new("plan", "builtins.plan")]);
        var definitions = new WorkflowDefinitionService(store, new(), integrations);
        var definition = await definitions.CreateAsync(new("Custom integration event"), default);
        await definitions.CreateVersionAsync(definition.Id, new(null, true, false, document), default);
        Assert.Equal("plan", WorkflowNodeDefinitions.Normalize(document).Triggers![0].NextStepId);
        Assert.Contains(WorkflowEventRegistry.Default.Catalog, item => item.Uses == uses && item.Title == "Custom event");
        var service = new WorkflowEventService(store, integrations, new(store, workflowDefinitions: definitions));
        var delivery = new DevOpsIssueLabelTriggerEvent(DevOpsProviderType.GitHub, "custom-delivery", "custom", "custom-action", repository.RepositoryUrl, "https://example.test/issues/1", "");
        Assert.Single(await service.HandleIssueEventAsync(delivery, default));
        Assert.Empty(await service.HandleIssueEventAsync(delivery with { DeliveryId = "ignored", Action = "other", IssueUrl = "https://example.test/issues/2" }, default));
    }
    private sealed class TestEvent(string uses) : IWorkflowEventDefinition
    {
        public WorkflowEventDescriptor Descriptor { get; } = new(uses, "Custom event", "Custom integration event", "Test", false, false, []);
        public WorkflowTriggerNodeSettings Compile(JsonElement configuration) => new(WorkflowTriggerType.Webhook, true, configuration.Deserialize<IssueEventSettings>(WorkflowEventDefinitions.JsonOptions)!.RepositoryIds!, null, WebhookSecretName: "test");
        public IReadOnlyList<string> Validate(JsonElement configuration) => [];
        public bool Matches(JsonElement configuration, WorkflowIntegrationEvent delivery) => delivery.EventName == "custom" && delivery.Action == "custom-action";
    }
}
