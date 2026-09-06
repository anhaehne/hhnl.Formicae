using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class EnvironmentDefinitionTests
{
    private static WorkflowDefinitionDocument Document(string? id = null) => new(DefaultWorkflowDefinitions.V1Alpha3Schema, "plan",
        [new("plan", "builtins.plan", "implement"), new("implement", "builtins.implement", "pr"), new("pr", "builtins.create-pull-request")],
        DefaultEnvironmentId: id);
    private static ExecutionEnvironmentProfile Profile(int revision = 3) => new()
    { Id = "custom", Name = "Bounded tasks", Revision = revision, ConfigurationJson = """{"schemaVersion":1,"runtime":{"timeoutLimitSeconds":60}}""" };
    private static EnvironmentSnapshot Snapshot() => new("custom", 3, "Bounded tasks", "", new() { Runtime = new(60) });

    [Theory]
    [InlineData("formicae.workflow/v1alpha1")]
    [InlineData("formicae.workflow/v1alpha2")]
    [InlineData("formicae.workflow/v1alpha3")]
    public async Task Legacy_documents_keep_default_behavior_without_catalog_or_backfill(string schema)
    {
        var document = Document() with { Schema = schema };
        Assert.True(EnvironmentDefinitions.ValidateRuntime(document).IsValid);
        Assert.Equal(EnvironmentService.DefaultSnapshot, EnvironmentDefinitions.ResolveForTask(document, document.Steps[0]));
        var resolved = await EnvironmentDefinitions.ResolveAsync(document, null, default);
        Assert.True(resolved.Validation.IsValid);
        Assert.Equal(EnvironmentService.DefaultSnapshot, resolved.Document.DefaultEnvironmentSnapshot);
        Assert.Null(document.DefaultEnvironmentSnapshot);
    }

    [Fact]
    public async Task Save_resolves_once_and_replaces_untrusted_snapshot_preserving_other_settings()
    {
        var store = new Store(Profile()) { ChangeAfterRead = true };
        var workflows = new InMemoryWorkflowStore();
        var service = new WorkflowDefinitionService(workflows, new(), environments: new EnvironmentService(store));
        var definition = await service.CreateAsync(new("Pinned environment"), default);
        var document = Document("custom") with { DefaultEnvironmentSnapshot = EnvironmentService.DefaultSnapshot,
            Editor = new(new Dictionary<string, WorkflowEditorPosition> { ["plan"] = new(21, 34) }, new(1, 2, .5)), DefaultPersonaId = "default" };
        var saved = await service.CreateVersionAsync(definition.Id, new(null, true, false, document), default);
        var persisted = WorkflowDefinitionJson.Deserialize((await workflows.GetWorkflowDefinitionVersionAsync(saved.Id, default))!.DefinitionJson)!;
        Assert.Equal(1, store.Reads);
        Assert.Equal(3, persisted.DefaultEnvironmentSnapshot!.Revision);
        Assert.Equal(60, persisted.DefaultEnvironmentSnapshot.Configuration.Runtime!.TimeoutLimitSeconds);
        Assert.Equal(document.Editor!.Positions["plan"], persisted.Editor!.Positions["plan"]);
        Assert.Equal(document.Editor.Viewport, persisted.Editor.Viewport);
        Assert.Equal("default", persisted.DefaultPersonaId);
        Assert.Equal(PersonaService.DefaultSnapshot, persisted.Steps[0].PersonaSnapshot);
        var normalized = WorkflowNodeDefinitions.Normalize(persisted);
        Assert.Equal(persisted.DefaultEnvironmentSnapshot, normalized.DefaultEnvironmentSnapshot);
        store.Value = Profile(4) with { IsDeleted = true };
        Assert.Equal(saved.Id, (await service.ResolveForRunAsync(definition.Id, saved.Id, default)).Id);
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task New_version_captures_current_revision_without_changing_prior_snapshot()
    {
        var store = new Store(Profile());
        var service = new EnvironmentService(store);
        var first = await EnvironmentDefinitions.ResolveAsync(Document("custom"), service, default);
        store.Value = Profile(4) with { ConfigurationJson = """{"runtime":{"timeoutLimitSeconds":30}}""" };
        var second = await EnvironmentDefinitions.ResolveAsync(first.Document, service, default);
        Assert.Equal(60, first.Document.DefaultEnvironmentSnapshot!.Configuration.Runtime!.TimeoutLimitSeconds);
        Assert.Equal(30, second.Document.DefaultEnvironmentSnapshot!.Configuration.Runtime!.TimeoutLimitSeconds);
        Assert.Equal(4, second.Document.DefaultEnvironmentSnapshot.Revision);
        store.Value = null;
        Assert.True(EnvironmentDefinitions.ValidateRuntime(first.Document).IsValid);
        Assert.True(EnvironmentDefinitions.ValidateRuntime(second.Document).IsValid);
    }

    [Fact]
    public async Task Disabled_missing_selection_keeps_reference_and_discards_snapshot_enabled_save_rejects()
    {
        var workflows = new InMemoryWorkflowStore();
        var service = new WorkflowDefinitionService(workflows, new(), environments: new EnvironmentService(new Store()));
        var definition = await service.CreateAsync(new("Incomplete environment"), default);
        var document = Document("custom") with { DefaultEnvironmentSnapshot = Snapshot() };
        var saved = await service.CreateVersionAsync(definition.Id, new(null, false, false, document), default);
        var persisted = WorkflowDefinitionJson.Deserialize((await workflows.GetWorkflowDefinitionVersionAsync(saved.Id, default))!.DefinitionJson)!;
        Assert.Equal("custom", persisted.DefaultEnvironmentId);
        Assert.Null(persisted.DefaultEnvironmentSnapshot);
        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() => service.CreateVersionAsync(definition.Id, new(null, true, false, document), default));
        Assert.Contains(exception.Errors, error => error.Path == "defaultEnvironmentId");
        Assert.Single(await workflows.ListWorkflowDefinitionVersionsAsync(definition.Id, default));
    }

    [Fact]
    public async Task Nonpersisting_validation_reads_current_reference_without_creating_workflows()
    {
        var workflows = new InMemoryWorkflowStore();
        var service = new WorkflowDefinitionService(workflows, new());
        var validation = await service.ValidateAsync(Document("missing") with { DefaultEnvironmentSnapshot = Snapshot() }, default);
        Assert.Contains(validation.Errors, error => error.Path == "defaultEnvironmentId");
        Assert.Empty(await workflows.ListWorkflowDefinitionsAsync(default));
    }

    [Theory]
    [InlineData("builtins.plan", true)]
    [InlineData("builtins.implement", true)]
    [InlineData("builtins.address-comments", true)]
    [InlineData("builtins.custom-task", true)]
    [InlineData("builtins.create-pull-request", false)]
    [InlineData("builtins.trigger", false)]
    [InlineData("builtins.loop", false)]
    [InlineData("builtins.parallel", false)]
    [InlineData("builtins.decision", false)]
    public void Only_ai_tasks_receive_the_selected_environment(string uses, bool receivesEnvironment)
    {
        var snapshot = Snapshot();
        var result = EnvironmentDefinitions.ResolveForTask(Document("custom") with { DefaultEnvironmentSnapshot = snapshot }, new("node", uses));
        if (receivesEnvironment) Assert.Same(snapshot, result);
        else Assert.Null(result);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("id")]
    [InlineData("revision")]
    [InlineData("name")]
    [InlineData("description")]
    [InlineData("configuration")]
    [InlineData("schema")]
    [InlineData("cap")]
    [InlineData("tools-null")]
    [InlineData("mcp-null")]
    [InlineData("image")]
    public void Missing_or_malformed_custom_snapshot_fails_before_task_launch(string scenario)
    {
        var snapshot = scenario switch
        {
            "absent" => null,
            "id" => Snapshot() with { Id = "other" },
            "revision" => Snapshot() with { Revision = 0 },
            "name" => Snapshot() with { Name = " " },
            "description" => Snapshot() with { Description = null! },
            "configuration" => Snapshot() with { Configuration = null! },
            "schema" => Snapshot() with { Configuration = new() { SchemaVersion = 2 } },
            "cap" => Snapshot() with { Configuration = new() { Runtime = new(3601) } },
            "tools-null" => Snapshot() with { Configuration = new() { Tools = null! } },
            "mcp-null" => Snapshot() with { Configuration = new() { McpServers = null! } },
            _ => Snapshot() with { Configuration = new() { Image = JsonSerializer.SerializeToElement("image:tag") } }
        };
        var document = Document("custom") with { DefaultEnvironmentSnapshot = snapshot };
        Assert.False(EnvironmentDefinitions.ValidateRuntime(document).IsValid);
        Assert.Throws<InvalidOperationException>(() => EnvironmentDefinitions.ResolveForTask(document, document.Steps[0]));
    }

    [Fact]
    public async Task Default_selection_cannot_smuggle_a_custom_profile_or_cap()
    {
        foreach (var snapshot in new[] { Snapshot(), EnvironmentService.DefaultSnapshot with { Revision = 2 },
                     EnvironmentService.DefaultSnapshot with { Configuration = new() { Runtime = new(20) } } })
        {
            var document = Document() with { DefaultEnvironmentSnapshot = snapshot };
            Assert.False(EnvironmentDefinitions.ValidateRuntime(document).IsValid);
            var resolved = await EnvironmentDefinitions.ResolveAsync(document, null, default);
            Assert.True(resolved.Validation.IsValid);
            Assert.Equal(EnvironmentService.DefaultSnapshot, resolved.Document.DefaultEnvironmentSnapshot);
        }
    }

    [Fact]
    public async Task Selection_is_validated_even_without_ai_nodes()
    {
        var document = Document("custom") with { StartStepId = "pr", Steps = [new("pr", "builtins.create-pull-request")] };
        Assert.True((await EnvironmentDefinitions.ResolveAsync(document, new EnvironmentService(new Store(Profile())), default)).Validation.IsValid);
        Assert.False((await EnvironmentDefinitions.ResolveAsync(document, null, default)).Validation.IsValid);
        Assert.False(EnvironmentDefinitions.ValidateRuntime(Document(" ")).IsValid);
    }

    private sealed class Store(ExecutionEnvironmentProfile? value = null) : IEnvironmentStore
    {
        public ExecutionEnvironmentProfile? Value { get; set; } = value;
        public int Reads { get; private set; }
        public bool ChangeAfterRead { get; set; }
        public Task<ExecutionEnvironmentProfile?> GetAsync(string id, CancellationToken token)
        {
            Reads++;
            var result = Value is { IsDeleted: false } && Value.Id == id ? Value : null;
            if (result is not null && ChangeAfterRead) Value = result with { Revision = result.Revision + 1 };
            return Task.FromResult(result);
        }
        public Task<IReadOnlyList<ExecutionEnvironmentProfile>> ListAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<ExecutionEnvironmentProfile> CreateAsync(ExecutionEnvironmentProfile profile, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> TryUpdateAsync(ExecutionEnvironmentProfile replacement, int expectedRevision, CancellationToken token) => throw new NotSupportedException();
    }
}
