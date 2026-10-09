using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Tests;

public sealed class AgentTaskDefinitionTests
{
    [Fact]
    public async Task Inline_and_catalog_tasks_include_the_same_pinned_output_contract_and_accept_legacy_preparation()
    {
        var outputs = new CustomTaskOutputDefinition[] { new("summary", "string", true), new("ready", "boolean") };
        var document = Document("Summarize") with { Steps = [new("agent", CustomTaskDefinitions.AgentUses,
            CustomTask: new("ignored", Definition: new("Summarize", [], new("agent", 60), outputs)))] };
        var resolved = await CustomTaskDefinitions.ResolveAsync(document, null, default);
        var inline = resolved.Document.Steps[0].CustomTask!;
        var catalog = inline with { TaskId = "catalog", Definition = null, Snapshot = inline.Snapshot! with { Id = "catalog" } };
        var workflow = new Workflow { IssueUrl = "https://example.test/issue/1", RepositoryUrl = "https://example.test/repo" };
        var preparation = CustomTaskDefinitions.Prepare(inline, workflow);
        Assert.Equal(preparation.Prompt, CustomTaskDefinitions.Prepare(catalog, workflow).Prompt);
        Assert.Contains("65536 UTF-8 bytes", preparation.Prompt);
        Assert.Contains("Include every required output", preparation.Prompt);
        Assert.Equal(preparation.Prompt, CustomTaskDefinitions.EnsureOutputInstruction(preparation.Prompt, outputs));
        var withPersona = preparation.Prompt + "\n\n## Persona guidance\nBe concise.";
        Assert.Equal(withPersona, CustomTaskDefinitions.EnsureOutputInstruction(withPersona, outputs));
        var legacy = "Summarize\n\nReturn your final response as one strict JSON object containing only the declared named outputs. Do not use markdown fences or additional commentary. Omit optional outputs when unavailable. Output schema: "
            + System.Text.Json.JsonSerializer.Serialize(outputs, System.Text.Json.JsonSerializerOptions.Web);
        CustomTaskDefinitions.ValidatePrepared(preparation with { Prompt = legacy }, inline);
        Assert.EndsWith(CustomTaskDefinitions.OutputInstruction(outputs), CustomTaskDefinitions.EnsureOutputInstruction(legacy, outputs));
        Assert.Throws<InvalidOperationException>(() => CustomTaskDefinitions.ValidatePrepared(preparation with { Prompt = "Forged" }, inline));
    }

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
