using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;
public sealed class WorkflowEditorTests
{
    private static WorkflowDefinitionDocument Document() => new(DefaultWorkflowDefinitions.V1Alpha3Schema, "plan", [new("plan", "builtins.plan")], Editor: new(new Dictionary<string, WorkflowEditorPosition> { ["plan"] = new(234, 567) }, new(10, 20, 0.75)));

    [Fact]
    public async Task Named_bindings_round_trip_with_pinned_output_schemas_and_control_edges()
    {
        var tasks = new CustomTaskService(new InMemoryCustomTaskStore());
        var producer = await tasks.CreateAsync(new("Producer", "Produce", Outputs: [new("summary", "string", true)]), default);
        var consumer = await tasks.CreateAsync(new("Consumer", "Use {{input.summary}}", Inputs: [new("summary", "string", true)]), default);
        var store = new InMemoryWorkflowStore(); var service = new WorkflowDefinitionService(store, new(), customTasks: tasks);
        var definition = await service.CreateAsync(new("Data"), default);
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "producer", [
            new("producer", CustomTaskDefinitions.Uses, "consumer", CustomTask: new(producer.Id)),
            new("consumer", CustomTaskDefinitions.Uses, CustomTask: new(consumer.Id, Bindings: new Dictionary<string, CustomTaskInputBinding> { ["summary"] = new("producer", "summary") }))]);
        var version = await service.CreateVersionAsync(definition.Id, new(null, true, false, document), default);
        var saved = WorkflowDefinitionJson.Deserialize((await store.GetWorkflowDefinitionVersionAsync(version.Id, default))!.DefinitionJson)!;
        Assert.Equal("consumer", saved.Steps[0].NextStepId); Assert.Null(saved.Steps[1].NextStepId);
        Assert.Equal(new("summary", "string", true), Assert.Single(saved.Steps[0].CustomTask!.Snapshot!.Outputs));
        Assert.Equal(new("producer", "summary"), saved.Steps[1].CustomTask!.Bindings!["summary"]);
        Assert.True(CustomTaskDefinitions.ValidateRuntime(saved).IsValid);
        await tasks.UpdateAsync(producer.Id, new(1, "Producer", "Produce", Outputs: [new("summary", "boolean")]), default);
        var validation = await service.ValidateAsync(saved, default);
        Assert.Contains(validation.Errors, error => error.NodeId == "consumer");
        Assert.True(CustomTaskDefinitions.ValidateRuntime(saved).IsValid);
    }

    [Fact]
    public async Task Editor_layout_round_trips_with_immutable_versions_and_is_ignored_by_execution()
    {
        var store = new InMemoryWorkflowStore();
        var service = new WorkflowDefinitionService(store, new());
        var definition = await service.CreateAsync(new("Layout"), default);
        var original = await service.CreateVersionAsync(definition.Id, new(null, true, false, Document()), default);
        var changed = Document() with { Editor = new(new Dictionary<string, WorkflowEditorPosition> { ["plan"] = new(100, 200) }) };
        await service.CreateVersionAsync(definition.Id, new(null, true, false, changed), default);
        var saved = WorkflowDefinitionJson.Deserialize((await store.GetWorkflowDefinitionVersionAsync(original.Id, default))!.DefinitionJson)!;
        Assert.Equal(new WorkflowEditorPosition(234, 567), saved.Editor!.Positions["plan"]);
        Assert.Equal(0.75, saved.Editor.Viewport!.Zoom);
        Assert.Equal(PersonaService.DefaultSnapshot, saved.Steps[0].PersonaSnapshot);
        Assert.Equal(WorkflowDefinitionJson.Serialize(WorkflowNodeDefinitions.Normalize(saved with { Editor = null })), WorkflowDefinitionJson.Serialize(WorkflowNodeDefinitions.Normalize(saved)));
    }

    [Fact]
    public async Task Named_groups_round_trip_without_changing_execution_or_older_versions()
    {
        var store = new InMemoryWorkflowStore();
        var service = new WorkflowDefinitionService(store, new());
        var definition = await service.CreateAsync(new("Groups"), default);
        var document = Document() with { Editor = Document().Editor! with {
            Groups = [new("group-1", "Planning", "purple", ["plan"])]
        } };
        var first = await service.CreateVersionAsync(definition.Id, new(null, true, false, document), default);
        await service.CreateVersionAsync(definition.Id, new(null, true, false, document with {
            Editor = document.Editor! with { Groups = [new("group-1", "Delivery", "blue", ["plan"])] }
        }), default);
        var saved = WorkflowDefinitionJson.Deserialize((await store.GetWorkflowDefinitionVersionAsync(first.Id, default))!.DefinitionJson)!;
        var group = Assert.Single(saved.Editor!.Groups!);
        Assert.Equal("group-1", group.Id);
        Assert.Equal("Planning", group.Name);
        Assert.Equal("purple", group.Color);
        Assert.Equal("plan", Assert.Single(group.NodeIds));
        Assert.Equal(document.Editor.Positions["plan"], saved.Editor.Positions["plan"]);
        Assert.Equal(WorkflowDefinitionJson.Serialize(WorkflowNodeDefinitions.Normalize(saved with { Editor = null })),
            WorkflowDefinitionJson.Serialize(WorkflowNodeDefinitions.Normalize(saved)));
    }

    [Fact]
    public async Task Validation_does_not_create_definitions_or_versions()
    {
        var store = new InMemoryWorkflowStore();
        var service = new WorkflowDefinitionService(store, new());
        Assert.True((await service.ValidateAsync(Document(), default)).IsValid);
        Assert.Empty(await store.ListWorkflowDefinitionsAsync(default));
    }

    [Fact]
    public async Task Incomplete_disabled_version_is_saved_but_enabled_save_returns_node_reference()
    {
        var store = new InMemoryWorkflowStore();
        var service = new WorkflowDefinitionService(store, new());
        var definition = await service.CreateAsync(new("Draft"), default);
        var document = Document() with { Steps = [new("plan", "builtins.plan", "missing")] };
        var result = await service.ValidateAsync(document, default);
        Assert.Contains(result.Errors, error => error.NodeId == "plan");
        await service.CreateVersionAsync(definition.Id, new(null, false, false, document), default);
        await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() => service.CreateVersionAsync(definition.Id, new(null, true, false, document), default));
        Assert.Single(await store.ListWorkflowDefinitionVersionsAsync(definition.Id, default));
    }

    [Fact]
    public void Legacy_definitions_without_editor_metadata_remain_valid()
    {
        var legacy = DefaultWorkflowDefinitions.CreateMvpDocument();
        Assert.True(new WorkflowDefinitionValidator().Validate(legacy).IsValid);
        Assert.Null(WorkflowDefinitionJson.Deserialize(WorkflowDefinitionJson.Serialize(legacy))!.Editor);
    }
}
