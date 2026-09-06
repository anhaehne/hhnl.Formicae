using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;
public sealed class WorkflowEditorTests
{
    private static WorkflowDefinitionDocument Document() => new(DefaultWorkflowDefinitions.V1Alpha3Schema, "plan", [new("plan", "builtins.plan")], Editor: new(new Dictionary<string, WorkflowEditorPosition> { ["plan"] = new(234, 567) }, new(10, 20, 0.75)));

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
        Assert.Equal(WorkflowDefinitionJson.Serialize(WorkflowNodeDefinitions.Normalize(Document() with { Editor = null })), WorkflowDefinitionJson.Serialize(WorkflowNodeDefinitions.Normalize(saved)));
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
