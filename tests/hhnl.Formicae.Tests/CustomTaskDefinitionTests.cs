using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class CustomTaskDefinitionTests
{
    private static JsonElement Value<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static CustomTaskSnapshot Snapshot(string prompt = "Review {{input.text}}", IReadOnlyList<CustomTaskInputDefinition>? inputs = null)
        => new("task", 1, "Reviewer", "", prompt, inputs ?? [new("text", "string", true)], new("agent", 40));
    private static WorkflowCustomTaskSettings Settings(CustomTaskSnapshot snapshot, params (string Key, JsonElement Value)[] values)
        => new(snapshot.Id, values.ToDictionary(pair => pair.Key, pair => pair.Value), snapshot);
    private static Workflow Workflow() => new() { IssueUrl = "https://example.test/issue/1", RepositoryUrl = "https://example.test/repo", PlanArtifact = "original plan" };
    private static WorkflowDefinitionDocument Document(WorkflowCustomTaskSettings settings) => new(DefaultWorkflowDefinitions.V1Alpha3Schema, "custom",
        [new("custom", CustomTaskDefinitions.Uses, CustomTask: settings)], Editor: new(new Dictionary<string, WorkflowEditorPosition> { ["custom"] = new(41, 83) }));

    [Theory]
    [InlineData("{{input.missing}}")]
    [InlineData("{{workflow.password}}")]
    [InlineData("{{input.text | trim}}")]
    [InlineData("{{ input.text}}")]
    [InlineData("{{input.text")]
    [InlineData("input.text}}")]
    [InlineData("}}{{input.text}}")]
    [InlineData("{{{{input.text}}}}")]
    [InlineData("{{input.text.more}}")]
    public void Template_tokens_are_strict_and_non_executable(string prompt)
        => Assert.False(CustomTaskDefinitions.ValidateCatalog("Task", "", prompt, [new("text", "string")], new()).IsValid);

    [Theory]
    [InlineData("_name")]
    [InlineData("1name")]
    [InlineData("name.x")]
    [InlineData("name\n")]
    [InlineData("name name")]
    public void Input_identifiers_are_exact(string name)
        => Assert.False(CustomTaskDefinitions.ValidateCatalog("Task", "", "Prompt", [new(name, "string")], new()).IsValid);

    [Fact]
    public void Rendering_is_single_pass_typed_and_captures_only_referenced_workflow_fields()
    {
        var snapshot = Snapshot("{literal} {{input.text}} {{input.count}} {{input.flag}} {{input.optional}} {{workflow.planArtifact}} {{workflow.model}}",
            [new("text", "string", true), new("count", "number", DefaultValue: Value(1.25m)), new("flag", "boolean", DefaultValue: Value(false)), new("optional", "string")]);
        var settings = Settings(snapshot, ("text", Value("{{workflow.issueUrl}}")));
        var workflow = Workflow();
        var prepared = CustomTaskDefinitions.Prepare(settings, workflow);
        Assert.Equal("{literal} {{workflow.issueUrl}} 1.25 false  original plan ", prepared.Prompt);
        Assert.Equal(["model", "planArtifact"], prepared.WorkflowFields.Keys.Order().ToArray());
        Assert.Equal(JsonValueKind.Null, prepared.WorkflowFields["model"].ValueKind);
        Assert.False(prepared.Inputs.ContainsKey("optional"));
        workflow.PlanArtifact = "later plan";
        CustomTaskDefinitions.ValidatePrepared(prepared, settings);
        Assert.Contains("original plan", prepared.Prompt);
        var roundTrip = JsonSerializer.Deserialize<PreparedCustomTaskExecution>(JsonSerializer.Serialize(prepared))!;
        CustomTaskDefinitions.ValidatePrepared(roundTrip, settings);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("null")]
    [InlineData("object")]
    [InlineData("array")]
    [InlineData("wrong-type")]
    [InlineData("long-string")]
    [InlineData("decimal-overflow")]
    public void Invalid_task_inputs_fail_before_preparation(string invalid)
    {
        var snapshot = Snapshot(inputs: [new("text", invalid == "decimal-overflow" ? "number" : "string", true)]);
        var values = new Dictionary<string, JsonElement>();
        if (invalid != "missing") values[invalid == "unknown" ? "other" : "text"] = invalid switch
        {
            "null" => Value<string?>(null), "object" => Value(new { x = 1 }), "array" => Value(new[] { 1 }),
            "wrong-type" => Value(true), "long-string" => Value(new string('x', 16001)),
            "decimal-overflow" => JsonDocument.Parse("1e100").RootElement.Clone(), _ => Value("value")
        };
        Assert.Throws<InvalidOperationException>(() => CustomTaskDefinitions.Prepare(new(snapshot.Id, values, snapshot), Workflow()));
    }

    [Fact]
    public void Required_empty_string_is_present_and_null_default_means_absent()
    {
        var snapshot = Snapshot("{{input.text}} {{input.optional}}", [new("text", "string", true), new("optional", "string", DefaultValue: Value<string?>(null))]);
        var prepared = CustomTaskDefinitions.Prepare(Settings(snapshot, ("text", Value(""))), Workflow());
        Assert.True(prepared.Inputs.ContainsKey("text")); Assert.False(prepared.Inputs.ContainsKey("optional"));
        Assert.Equal(" ", prepared.Prompt); // Final prompt/persona validation belongs to the launch boundary.
    }

    [Fact]
    public void Aggregate_input_and_rendered_prompt_byte_limits_are_enforced()
    {
        var inputs = Enumerable.Range(0, 5).Select(i => new CustomTaskInputDefinition($"input{i}", "string", true)).ToArray();
        var settings = Settings(Snapshot("Prompt", inputs), inputs.Select(input => (input.Name, Value(new string('x', 16000)))).ToArray());
        Assert.Throws<InvalidOperationException>(() => CustomTaskDefinitions.Prepare(settings, Workflow()));
        var repeat = Snapshot(string.Concat(Enumerable.Repeat("{{input.text}}", 9)));
        Assert.Throws<InvalidOperationException>(() => CustomTaskDefinitions.Prepare(Settings(repeat, ("text", Value(new string('x', 16000)))), Workflow()));
        var unicode = Snapshot("{{workflow.planArtifact}}", []);
        var workflow = Workflow(); workflow.PlanArtifact = new string('界', 50000);
        Assert.Throws<InvalidOperationException>(() => CustomTaskDefinitions.Prepare(Settings(unicode), workflow));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("identity")]
    [InlineData("revision")]
    [InlineData("inputs")]
    [InlineData("fields")]
    [InlineData("prompt")]
    [InlineData("timeout")]
    public void Stored_execution_corruption_never_recaptures_live_values(string corruption)
    {
        var settings = Settings(Snapshot(), ("text", Value("original")));
        var prepared = CustomTaskDefinitions.Prepare(settings, Workflow());
        prepared = corruption switch
        {
            "version" => prepared with { FormatVersion = 2 }, "identity" => prepared with { TaskId = "other" },
            "revision" => prepared with { Revision = 2 }, "inputs" => prepared with { Inputs = new Dictionary<string, JsonElement> { ["text"] = Value("changed") } },
            "fields" => prepared with { WorkflowFields = new Dictionary<string, JsonElement> { ["password"] = Value("extra") } },
            "prompt" => prepared with { Prompt = "changed" }, _ => prepared with { TimeoutSeconds = 41 }
        };
        Assert.Throws<InvalidOperationException>(() => CustomTaskDefinitions.ValidatePrepared(prepared, settings));
    }

    [Fact]
    public async Task Version_snapshots_replace_forged_data_preserve_layout_and_remain_runnable_after_catalog_deletion()
    {
        var catalog = new CustomTaskService(new InMemoryCustomTaskStore());
        var task = await catalog.CreateAsync(new("Task", "First {{input.text}}", Inputs: [new("text", "string", true)]), default);
        var store = new InMemoryWorkflowStore();
        var service = new WorkflowDefinitionService(store, new(), customTasks: catalog);
        var definition = await service.CreateAsync(new("Custom workflow"), default);
        var document = Document(new(task.Id, new Dictionary<string, JsonElement> { ["text"] = Value("input") }, Snapshot("Forged")));
        var first = await service.CreateVersionAsync(definition.Id, new(null, true, false, document), default);
        var saved = first.Definition;
        Assert.Equal("First {{input.text}}", saved.Steps[0].CustomTask!.Snapshot!.PromptTemplate);
        Assert.Equal(PersonaService.DefaultSnapshot, saved.Steps[0].PersonaSnapshot);
        Assert.Equal(new WorkflowEditorPosition(41, 83), saved.Editor!.Positions["custom"]);
        await catalog.UpdateAsync(task.Id, new(1, "Task", "Second {{input.text}}", Inputs: [new("text", "string", true)]), default);
        var second = await service.CreateVersionAsync(definition.Id, new(null, true, false, document), default);
        Assert.Equal(2, second.Definition.Steps[0].CustomTask!.Snapshot!.Revision);
        await catalog.DeleteAsync(task.Id, 2, default);
        Assert.Equal(first.Id, (await service.ResolveForRunAsync(definition.Id, first.Id, default)).Id);
        Assert.Equal("First input", CustomTaskDefinitions.Prepare(saved.Steps[0].CustomTask!, Workflow()).Prompt);
        await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() => service.CreateVersionAsync(definition.Id, new(null, true, false, saved), default));
        var disabled = await service.CreateVersionAsync(definition.Id, new(null, false, false, saved), default);
        Assert.Equal(task.Id, disabled.Definition.Steps[0].CustomTask!.TaskId);
        Assert.Null(disabled.Definition.Steps[0].CustomTask!.Snapshot);
    }

    [Fact]
    public async Task Repeated_catalog_ids_are_resolved_once_and_ignore_submitted_revisions()
    {
        var store = new ChangingStore();
        var catalog = new CustomTaskService(store);
        var document = Document(new("task", Snapshot: Snapshot("Forged"))) with
        { Steps = [new("a", CustomTaskDefinitions.Uses, "b", CustomTask: new("task")), new("b", CustomTaskDefinitions.Uses, CustomTask: new("task"))], StartStepId = "a" };
        var resolution = await CustomTaskDefinitions.ResolveAsync(document, catalog, default);
        Assert.True(resolution.Validation.IsValid); Assert.Equal(1, store.Reads);
        Assert.All(resolution.Document.Steps, step => Assert.Equal(1, step.CustomTask!.Snapshot!.Revision));
    }

    [Theory]
    [InlineData("formicae.workflow/v1alpha1")]
    [InlineData("formicae.workflow/v1alpha2")]
    [InlineData("formicae.workflow/v1alpha3")]
    public void Custom_settings_on_builtin_tasks_are_rejected_in_every_schema(string schema)
    {
        var document = Document(Settings(Snapshot(), ("text", Value("data")))) with { Schema = schema };
        document = document with { Steps = [document.Steps[0] with { Uses = "builtins.plan" }] };
        Assert.False(new WorkflowDefinitionValidator().Validate(document).IsValid);
        Assert.False(CustomTaskDefinitions.ValidateRuntime(document).IsValid);
    }

    [Fact]
    public void Custom_nodes_normalize_inside_loops_but_remain_forbidden_in_parallel_branches()
    {
        var settings = Settings(Snapshot(), ("text", Value("data")));
        var loop = Document(settings) with { StartStepId = "loop", Steps = [
            new("loop", WorkflowNodeDefinitions.LoopUses, "exit", Loop: new("custom", 2, 2)),
            new("custom", CustomTaskDefinitions.Uses, "loop", NextStepPort: "return", CustomTask: settings), new("exit", "builtins.plan") ] };
        Assert.True(new WorkflowDefinitionValidator().Validate(loop).IsValid);
        Assert.Equal(settings, WorkflowNodeDefinitions.Normalize(loop).Steps.Single(step => step.Id == "custom").CustomTask);
        var parallel = loop with { StartStepId = "fork", Steps = [
            new("fork", WorkflowParallelDefinitions.Uses, "exit", Parallel: new(["custom", "plan"])),
            new("custom", CustomTaskDefinitions.Uses, "fork", NextStepPort: "join", CustomTask: settings),
            new("plan", "builtins.plan", "fork", NextStepPort: "join"), new("exit", "builtins.plan") ] };
        Assert.False(new WorkflowDefinitionValidator().Validate(parallel).IsValid);
    }

    [Fact]
    public void Custom_tasks_accept_personas_and_history_survives_malformed_execution_metadata()
    {
        Assert.True(PersonaDefinitions.IsAiTask(CustomTaskDefinitions.Uses));
        var run = new TaskRun { WorkflowId = Guid.NewGuid(), Kind = TaskRunKind.Custom, CustomTaskExecutionJson = "{broken", Output = "retained" };
        var response = run.ToResponse(); Assert.Null(response.CustomTaskExecution); Assert.Equal("retained", response.Output);
        run.CustomTaskExecutionJson = JsonSerializer.Serialize(CustomTaskDefinitions.Prepare(Settings(Snapshot(), ("text", Value("data"))), Workflow()), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("Reviewer", run.ToResponse().CustomTaskExecution!.Name);
    }

    private sealed class ChangingStore : ICustomTaskStore
    {
        public int Reads { get; private set; }
        public Task<CustomTaskDefinition?> GetAsync(string id, CancellationToken token) => Task.FromResult<CustomTaskDefinition?>(new CustomTaskDefinition
        { Id = "task", Name = "Task", PromptTemplate = "Prompt", Revision = ++Reads });
        public Task<IReadOnlyList<CustomTaskDefinition>> ListAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<CustomTaskDefinition> CreateAsync(CustomTaskDefinition task, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> TryUpdateAsync(CustomTaskDefinition replacement, int expectedRevision, CancellationToken token) => throw new NotSupportedException();
    }
}
