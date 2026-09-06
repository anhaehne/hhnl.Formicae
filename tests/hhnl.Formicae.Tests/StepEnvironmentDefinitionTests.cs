using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class StepEnvironmentDefinitionTests
{
    private static WorkflowDefinitionDocument Document(string? defaultId = "workflow") => new(DefaultWorkflowDefinitions.V1Alpha3Schema, "inherit",
        [new("inherit", "builtins.plan", "override"), new("override", "builtins.implement", "default", EnvironmentId: "step"),
            new("default", "builtins.address-comments", "pr", EnvironmentId: "default"), new("pr", "builtins.create-pull-request")],
        DefaultEnvironmentId: defaultId);
    private static ExecutionEnvironmentProfile Profile(string id, int cap) => new()
    { Id = id, Name = id, Revision = 2, ConfigurationJson = System.Text.Json.JsonSerializer.Serialize(new { runtime = new { timeoutLimitSeconds = cap } }) };
    private static EnvironmentSnapshot Snapshot(string id, int cap = 30) => new(id, 2, id, "", new() { Runtime = new(cap) });
    private static WorkflowDefinitionDocument Change(WorkflowDefinitionDocument document, string id, Func<WorkflowDefinitionStep, WorkflowDefinitionStep> edit)
        => document with { Steps = document.Steps.Select(step => step.Id == id ? edit(step) : step).ToArray() };

    [Fact]
    public async Task Inheritance_override_and_explicit_default_are_distinct_and_resolve_each_id_once()
    {
        var store = new Store(Profile("workflow", 60), Profile("step", 30)) { ChangeAfterRead = true };
        var document = Document() with { DefaultEnvironmentSnapshot = Snapshot("forged") };
        document = Change(document, "inherit", step => step with { EnvironmentSnapshot = Snapshot("forged") });
        var result = await EnvironmentDefinitions.ResolveAsync(document, new(store), default);
        Assert.True(result.Validation.IsValid);
        Assert.Equal(1, store.Reads["workflow"]); Assert.Equal(1, store.Reads["step"]);
        Assert.Equal(60, result.Document.Steps[0].EnvironmentSnapshot!.Configuration.Runtime!.TimeoutLimitSeconds);
        Assert.Null(result.Document.Steps[0].EnvironmentId);
        Assert.Equal(30, result.Document.Steps[1].EnvironmentSnapshot!.Configuration.Runtime!.TimeoutLimitSeconds);
        Assert.Equal("step", result.Document.Steps[1].EnvironmentId);
        Assert.Equal(EnvironmentService.DefaultSnapshot, result.Document.Steps[2].EnvironmentSnapshot);
        Assert.Equal("default", result.Document.Steps[2].EnvironmentId);
        Assert.Null(result.Document.Steps[3].EnvironmentSnapshot);
        Assert.All(result.Document.Steps.Take(2), step => Assert.Equal(2, step.EnvironmentSnapshot!.Revision));
    }

    [Fact]
    public async Task Explicit_reference_matching_workflow_default_shares_the_same_catalog_read()
    {
        var store = new Store(Profile("workflow", 60));
        var result = await EnvironmentDefinitions.ResolveAsync(Change(Document(), "override", step => step with { EnvironmentId = "workflow" }), new(store), default);
        Assert.True(result.Validation.IsValid); Assert.Equal(1, store.Reads["workflow"]);
        Assert.Same(result.Document.DefaultEnvironmentSnapshot, result.Document.Steps[1].EnvironmentSnapshot);
    }

    [Fact]
    public async Task Missing_references_are_cached_but_each_affected_node_has_a_locatable_error()
    {
        var store = new Store();
        var document = Change(Document("missing"), "override", step => step with { EnvironmentId = "missing", EnvironmentSnapshot = Snapshot("missing") });
        var result = await EnvironmentDefinitions.ResolveAsync(document, new(store), default);
        Assert.Equal(1, store.Reads["missing"]);
        Assert.Contains(result.Validation.Errors, error => error.Path == "defaultEnvironmentId" && error.NodeId is null);
        Assert.Contains(result.Validation.Errors, error => error.NodeId == "inherit");
        Assert.Contains(result.Validation.Errors, error => error.NodeId == "override");
        Assert.All(result.Document.Steps.Take(2), step => Assert.Null(step.EnvironmentSnapshot));
        Assert.Equal(EnvironmentService.DefaultSnapshot, result.Document.Steps[2].EnvironmentSnapshot);
    }

    [Fact]
    public async Task Invalid_workflow_default_is_rejected_even_when_every_task_overrides_it()
    {
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "plan",
            [new("plan", "builtins.plan", EnvironmentId: "default")], DefaultEnvironmentId: "missing");
        var result = await EnvironmentDefinitions.ResolveAsync(document, new(new Store()), default);
        Assert.Contains(result.Validation.Errors, error => error.Path == "defaultEnvironmentId");
        Assert.Equal(EnvironmentService.DefaultSnapshot, result.Document.Steps[0].EnvironmentSnapshot);
    }

    [Theory]
    [InlineData("builtins.create-pull-request")]
    [InlineData("builtins.trigger")]
    [InlineData("builtins.loop")]
    [InlineData("builtins.parallel")]
    [InlineData("builtins.decision")]
    public async Task Non_ai_nodes_reject_selections_and_strip_untrusted_snapshots(string uses)
    {
        var document = Document(null) with { Steps = [new("node", uses, EnvironmentId: "default", EnvironmentSnapshot: Snapshot("forged"))] };
        var result = await EnvironmentDefinitions.ResolveAsync(document, null, default);
        Assert.Contains(result.Validation.Errors, error => error.NodeId == "node");
        Assert.Equal("default", result.Document.Steps[0].EnvironmentId); Assert.Null(result.Document.Steps[0].EnvironmentSnapshot);
        Assert.False(EnvironmentDefinitions.ValidateRuntime(document).IsValid);
        Assert.False(EnvironmentDefinitions.ValidateRuntime(Change(document, "node", step => step with { EnvironmentId = null })).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Explicit_custom_snapshot_is_required_and_blank_references_do_not_inherit(string? id)
    {
        var step = new WorkflowDefinitionStep("plan", "builtins.plan", EnvironmentId: id ?? "custom");
        var document = Document(null) with { Steps = [step] };
        Assert.Contains(EnvironmentDefinitions.ValidateRuntime(document).Errors, error => error.NodeId == "plan");
        Assert.Throws<InvalidOperationException>(() => EnvironmentDefinitions.ResolveForTask(document, step));
    }

    [Fact]
    public void Legacy_step_metadata_inherits_pinned_workflow_profile_and_explicit_default_opts_out()
    {
        var document = Document("workflow") with { DefaultEnvironmentSnapshot = Snapshot("workflow", 60),
            Steps = [new("inherited", "builtins.plan"), new("default", "builtins.plan", EnvironmentId: "default")] };
        Assert.True(EnvironmentDefinitions.ValidateRuntime(document).IsValid);
        Assert.Equal(60, EnvironmentDefinitions.ResolveForTask(document, document.Steps[0])!.Configuration.Runtime!.TimeoutLimitSeconds);
        Assert.Null(EnvironmentDefinitions.ResolveForTask(document, document.Steps[1])!.Configuration.Runtime?.TimeoutLimitSeconds);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("revision")]
    [InlineData("name")]
    [InlineData("cap")]
    [InlineData("null-config")]
    [InlineData("default-forgery")]
    public void Present_step_snapshot_must_match_selection_and_all_same_id_snapshots(string scenario)
    {
        var snapshot = scenario switch
        {
            "id" => Snapshot("other"),
            "revision" => Snapshot("workflow", 60) with { Revision = 3 },
            "name" => Snapshot("workflow", 60) with { Name = "Different" },
            "cap" => Snapshot("workflow", 30),
            "null-config" => Snapshot("workflow", 60) with { Configuration = null! },
            _ => EnvironmentService.DefaultSnapshot with { Configuration = new() { Runtime = new(10) } }
        };
        var document = Document() with { DefaultEnvironmentSnapshot = Snapshot("workflow", 60),
            Steps = [new("plan", "builtins.plan", EnvironmentId: scenario == "default-forgery" ? "default" : null, EnvironmentSnapshot: snapshot)] };
        Assert.Contains(EnvironmentDefinitions.ValidateRuntime(document).Errors, error => error.NodeId == "plan");
    }

    [Fact]
    public void Same_override_id_cannot_have_different_configurations_on_two_steps()
    {
        var document = Document(null) with { Steps = [new("a", "builtins.plan", EnvironmentId: "step", EnvironmentSnapshot: Snapshot("step", 30)),
            new("b", "builtins.plan", EnvironmentId: "step", EnvironmentSnapshot: Snapshot("step", 60))] };
        Assert.Contains(EnvironmentDefinitions.ValidateRuntime(document).Errors, error => error.Code == "definition.environment.snapshot.conflict" && error.NodeId == "b");
    }

    [Fact]
    public void Equivalent_json_configurations_compare_semantically_after_round_trip()
    {
        var snapshot = new EnvironmentSnapshot("workflow", 1, "Empty", "", new());
        var document = Document() with { StartStepId = "plan", DefaultEnvironmentSnapshot = snapshot,
            Steps = [new("plan", "builtins.plan", EnvironmentSnapshot: snapshot with { Configuration = new() { Runtime = new() } })] };
        var persisted = WorkflowDefinitionJson.Deserialize(WorkflowDefinitionJson.Serialize(document))!;
        Assert.True(EnvironmentDefinitions.ValidateRuntime(persisted).IsValid);
        var normalized = WorkflowNodeDefinitions.Normalize(persisted);
        Assert.Equal(persisted.Steps[0].EnvironmentSnapshot, normalized.Steps[0].EnvironmentSnapshot);
    }

    [Fact]
    public async Task Saved_versions_and_step_history_stay_pinned_after_edit_and_delete()
    {
        var store = new Store(Profile("workflow", 60), Profile("step", 30));
        var workflows = new InMemoryWorkflowStore();
        var service = new WorkflowDefinitionService(workflows, new(), environments: new(store));
        var definition = await service.CreateAsync(new("Step environments"), default);
        var first = await service.CreateVersionAsync(definition.Id, new(null, true, false, Document()), default);
        store.Values["step"] = Profile("step", 10) with { Revision = 3 };
        var second = await service.CreateVersionAsync(definition.Id, new(null, true, false, first.Definition), default);
        Assert.Equal(30, first.Definition.Steps[1].EnvironmentSnapshot!.Configuration.Runtime!.TimeoutLimitSeconds);
        Assert.Equal(10, second.Definition.Steps[1].EnvironmentSnapshot!.Configuration.Runtime!.TimeoutLimitSeconds);
        store.Values.Clear();
        Assert.Equal(first.Id, (await service.ResolveForRunAsync(definition.Id, first.Id, default)).Id);
        Assert.Equal(second.Id, (await service.ResolveForRunAsync(definition.Id, second.Id, default)).Id);
        var disabled = await service.CreateVersionAsync(definition.Id, new(null, false, false, first.Definition), default);
        Assert.Equal("step", disabled.Definition.Steps[1].EnvironmentId); Assert.Null(disabled.Definition.Steps[1].EnvironmentSnapshot);
        var defaultSnapshot = disabled.Definition.Steps[2].EnvironmentSnapshot!;
        Assert.Equal(EnvironmentService.DefaultEnvironmentId, defaultSnapshot.Id);
        Assert.Equal(1, defaultSnapshot.Revision);
        Assert.Null(defaultSnapshot.Configuration.Runtime?.TimeoutLimitSeconds);
        var invalid = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() => service.CreateVersionAsync(definition.Id, new(null, true, false, first.Definition), default));
        Assert.Contains(invalid.Errors, error => error.NodeId == "override");
    }

    [Fact]
    public async Task Nonpersisting_validation_returns_node_reference_and_never_creates_versions()
    {
        var workflows = new InMemoryWorkflowStore(); var service = new WorkflowDefinitionService(workflows, new());
        var document = Document(null) with { StartStepId = "node", Steps = [new("node", "builtins.plan", EnvironmentId: "missing", EnvironmentSnapshot: Snapshot("missing"))] };
        Assert.Contains((await service.ValidateAsync(document, default)).Errors, error => error.NodeId == "node" && error.Path == "steps[].environmentId");
        Assert.Empty(await workflows.ListWorkflowDefinitionsAsync(default));
    }

    [Fact]
    public async Task Malformed_steps_are_validation_errors_without_null_reference_failures()
    {
        foreach (var steps in new IReadOnlyList<WorkflowDefinitionStep>[] { null!, [null!] })
        {
            var document = Document(null) with { Steps = steps };
            Assert.False((await EnvironmentDefinitions.ResolveAsync(document, null, default)).Validation.IsValid);
            Assert.False(EnvironmentDefinitions.ValidateRuntime(document).IsValid);
        }
    }

    private sealed class Store(params ExecutionEnvironmentProfile[] values) : IEnvironmentStore
    {
        public Dictionary<string, ExecutionEnvironmentProfile> Values { get; } = values.ToDictionary(value => value.Id);
        public Dictionary<string, int> Reads { get; } = [];
        public bool ChangeAfterRead { get; set; }
        public Task<ExecutionEnvironmentProfile?> GetAsync(string id, CancellationToken token)
        {
            Reads[id] = Reads.GetValueOrDefault(id) + 1;
            Values.TryGetValue(id, out var value);
            if (value is not null && ChangeAfterRead) Values[id] = value with { Revision = value.Revision + 1 };
            return Task.FromResult(value);
        }
        public Task<IReadOnlyList<ExecutionEnvironmentProfile>> ListAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<ExecutionEnvironmentProfile> CreateAsync(ExecutionEnvironmentProfile profile, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> TryUpdateAsync(ExecutionEnvironmentProfile replacement, int expectedRevision, CancellationToken token) => throw new NotSupportedException();
    }
}
