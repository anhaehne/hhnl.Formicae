using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Tests;

public sealed class AgentTaskDefinitionTests
{
    private static WorkflowDefinitionDocument Document(string prompt = "Review {{workflow.issueUrl}}") =>
        new(DefaultWorkflowDefinitions.V1Alpha3Schema, "agent", [new("agent", CustomTaskDefinitions.AgentUses,
            CustomTask: new("ignored", Definition: new(prompt, [], new("agent", 60))))]);

    [Fact]
    public async Task Inline_task_resolves_without_catalog_and_pins_persona_and_execution_settings()
    {
        var persona = await PersonaDefinitions.ResolveAsync(Document(), null, default);
        Assert.True(persona.Validation.IsValid);
        var resolved = await CustomTaskDefinitions.ResolveAsync(persona.Document, null, default);
        Assert.True(resolved.Validation.IsValid);
        Assert.True(new WorkflowDefinitionValidator().Validate(resolved.Document).IsValid);
        Assert.True(CustomTaskDefinitions.ValidateRuntime(resolved.Document).IsValid);
        var step = Assert.Single(resolved.Document.Steps);
        Assert.Equal("default", step.PersonaSnapshot!.Id);
        Assert.Equal("agent:agent", step.CustomTask!.Snapshot!.Id);
        Assert.True(WorkflowDefinitionValidator.TryMapUsesToTaskKind(step.Uses, out var kind));
        Assert.Equal(TaskRunKind.Custom, kind);
        var prepared = CustomTaskDefinitions.Prepare(step.CustomTask, new() { IssueUrl = "https://example.test/issue/1", RepositoryUrl = "https://example.test/repo" });
        Assert.Equal("Review https://example.test/issue/1", prepared.Prompt);
        Assert.Equal(60, prepared.TimeoutSeconds);
    }

    [Fact]
    public async Task Supplied_snapshot_is_replaced_and_runtime_detects_changed_inline_definition()
    {
        var doc = Document();
        var step = doc.Steps[0];
        doc = doc with { Steps = [step with { CustomTask = step.CustomTask! with {
            Snapshot = new("forged", 99, "Forged", "", "Wrong prompt", [], new()) } }] };
        var resolved = await CustomTaskDefinitions.ResolveAsync(doc, null, default);
        Assert.True(resolved.Validation.IsValid);
        var saved = resolved.Document.Steps[0];
        Assert.Equal("Review {{workflow.issueUrl}}", saved.CustomTask!.Snapshot!.PromptTemplate);
        var tampered = resolved.Document with { Steps = [saved with { CustomTask = saved.CustomTask with {
            Definition = saved.CustomTask.Definition! with { PromptTemplate = "Changed" } } }] };
        Assert.False(CustomTaskDefinitions.ValidateRuntime(tampered).IsValid);
    }

    [Theory]
    [InlineData("{{input.missing}}")]
    [InlineData("")]
    public async Task Inline_prompts_use_existing_validation(string prompt)
    {
        var resolved = await CustomTaskDefinitions.ResolveAsync(Document(prompt), null, default);
        Assert.False(resolved.Validation.IsValid);
    }

    [Fact]
    public async Task Agent_nodes_require_inline_settings()
    {
        var doc = Document() with { Steps = [new("agent", CustomTaskDefinitions.AgentUses)] };
        Assert.False((await CustomTaskDefinitions.ResolveAsync(doc, null, default)).Validation.IsValid);
        Assert.False(new WorkflowDefinitionValidator().Validate(doc).IsValid);
    }
}
