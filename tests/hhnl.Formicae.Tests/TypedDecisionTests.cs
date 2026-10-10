using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Infrastructure.Prompts;

namespace hhnl.Formicae.Tests;

public sealed class TypedDecisionTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static WorkflowDecisionNodeSettings Settings(string type) => new(
        new("literal", "boolean", "equals", Value: Json(true), CompareTo: Json(true)), "yes", "no",
        InputType: type, InputBinding: new("source", "value"), Cases: type == "boolean" ? [] : [new("first", "equals", Json(type == "string" ? (object)"ready" : 4), "yes")],
        DefaultStepId: type == "boolean" ? null : "no");
    private static CustomTaskInputProvenance Source(object value) => new("source", "value", Guid.NewGuid(), Guid.NewGuid(), null, Json(value));

    [Theory]
    [InlineData(true, "yes")]
    [InlineData(false, "no")]
    public void Boolean_input_selects_its_exit_directly(bool value, string target)
        => Assert.Equal(target, WorkflowDecisionEvaluator.EvaluateInput(Settings("boolean"), Source(value)).Target);

    [Theory]
    [InlineData("ready", "yes")]
    [InlineData("Ready", "no")]
    [InlineData("", "no")]
    [InlineData("other", "no")]
    public void String_input_matches_exactly_or_uses_default(string value, string target)
        => Assert.Equal(target, WorkflowDecisionEvaluator.EvaluateInput(Settings("string"), Source(value)).Target);

    [Theory]
    [InlineData("equals", 4, true)]
    [InlineData("notEquals", 3, true)]
    [InlineData("notEquals", 4, false)]
    [InlineData("greaterThan", 5, true)]
    [InlineData("greaterThan", 4, false)]
    [InlineData("greaterThanOrEqual", 4, true)]
    [InlineData("lessThan", 3, true)]
    [InlineData("lessThan", 4, false)]
    [InlineData("lessThanOrEqual", 4, true)]
    public void Numeric_comparisons_route_with_boundaries(string op, int value, bool matches)
    {
        var settings = Settings("number") with { Cases = [new("first", op, Json(4), "yes")] };
        Assert.Equal(matches ? "yes" : "no", WorkflowDecisionEvaluator.EvaluateInput(settings, Source(value)).Target);
    }

    [Fact]
    public void Overlapping_numeric_cases_use_first_match_and_record_port_and_evidence()
    {
        var settings = Settings("number") with { Cases = [new("first", "greaterThan", Json(3), "yes"), new("second", "notEquals", Json(4), "other")] };
        var result = WorkflowDecisionEvaluator.EvaluateInput(settings, Source(5));
        Assert.Equal("yes", result.Target);
        using var evidence = JsonDocument.Parse(result.InputJson);
        Assert.Equal("case:first", evidence.RootElement.GetProperty("selectedPort").GetString());
        Assert.Equal(5, evidence.RootElement.GetProperty("provenance").GetProperty("value").GetInt32());
    }

    [Fact]
    public void Incomplete_and_invalid_cases_are_rejected()
    {
        Assert.False(WorkflowDecisionDefinitions.ValidateInput(Settings("any")).IsValid);
        Assert.False(WorkflowDecisionDefinitions.ValidateInput(Settings("boolean") with { InputBinding = null }).IsValid);
        Assert.False(WorkflowDecisionDefinitions.ValidateInput(Settings("string") with { DefaultStepId = null }).IsValid);
        Assert.False(WorkflowDecisionDefinitions.ValidateInput(Settings("string") with { Cases = [new("a", "equals", Json("x"), "yes"), new("b", "equals", Json("x"), "no")] }).IsValid);
        Assert.False(WorkflowDecisionDefinitions.ValidateInput(Settings("number") with { Cases = [new("a", "contains", Json(3), "yes")] }).IsValid);
        Assert.False(WorkflowDecisionDefinitions.ValidateInput(Settings("number") with { Cases = [new("a", "equals", JsonDocument.Parse("1e-100").RootElement.Clone(), "yes")] }).IsValid);
        Assert.False(WorkflowDecisionDefinitions.ValidateInput(Settings("number") with { Cases = [new("a", "equals", JsonDocument.Parse("9007199254740993").RootElement.Clone(), "yes")] }).IsValid);
        Assert.Throws<InvalidOperationException>(() => WorkflowDecisionEvaluator.EvaluateInput(Settings("boolean"), Source("true")));
        Assert.Throws<InvalidOperationException>(() => WorkflowDecisionEvaluator.EvaluateInput(Settings("string"), Source(3)));
    }

    [Fact]
    public async Task Typed_binding_validation_rejects_stale_types_and_conditional_producers()
    {
        var catalog = new CustomTaskService(new InMemoryCustomTaskStore());
        var producer = await catalog.CreateAsync(new("Producer", "Return value", Outputs: [new("value", "string", true)]), default);
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "source", [
            new("source", CustomTaskDefinitions.Uses, "choose", CustomTask: new(producer.Id, Snapshot: new CustomTaskSnapshot(producer.Id, producer.Revision, producer.Name, producer.Description, producer.PromptTemplate, producer.Inputs, producer.Runner, producer.Outputs))),
            new("choose", WorkflowDecisionDefinitions.Uses, Decision: Settings("string")),
            new("yes", WorkflowEndDefinitions.Uses), new("no", WorkflowEndDefinitions.Uses)]);
        Assert.True(CustomTaskDefinitions.ValidateRuntime(document).IsValid);
        var stale = document with { Steps = document.Steps.Select(step => step.Id == "choose" ? step with { Decision = Settings("number") } : step).ToArray() };
        Assert.False(CustomTaskDefinitions.ValidateRuntime(stale).IsValid);
        var unavailable = document with { StartStepId = "choose" };
        Assert.False(CustomTaskDefinitions.ValidateRuntime(unavailable).IsValid);
    }

    [Theory]
    [InlineData("string", "ready", "yes", false)]
    [InlineData("string", "ready", "yes", true)]
    [InlineData("string", "unknown", "no", false)]
    [InlineData("string", "unknown", "no", true)]
    [InlineData("boolean", "true", "yes", false)]
    [InlineData("boolean", "true", "yes", true)]
    [InlineData("boolean", "false", "no", false)]
    [InlineData("boolean", "false", "no", true)]
    [InlineData("number", "4", "yes", false)]
    [InlineData("number", "4", "yes", true)]
    [InlineData("number", "5", "no", false)]
    [InlineData("number", "5", "no", true)]
    public async Task Orchestrator_persists_typed_route_and_runs_only_selected_end(string type, string value, string target, bool variable)
    {
        var catalog = new CustomTaskService(new InMemoryCustomTaskStore());
        var producer = await catalog.CreateAsync(new("Producer", "Return value", Outputs: [new("value", type, true)]), default);
        var store = new InMemoryWorkflowStore(); var definitions = new WorkflowDefinitionService(store, new(), customTasks: catalog);
        var definition = await definitions.CreateAsync(new("Typed decision"), default);
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "source", [
            new("source", CustomTaskDefinitions.Uses, "choose", CustomTask: new(producer.Id)),
            new("choose", WorkflowDecisionDefinitions.Uses, Decision: Settings(type)),
            new("yes", WorkflowEndDefinitions.Uses), new("no", WorkflowEndDefinitions.Uses)]);
        if (variable) document = document with { Variables = [new("variable", "Decision value", type, Sources: [new("source", "value")])],
            Steps = document.Steps.Select(step => step.Id == "choose" ? step with { Decision = step.Decision! with { InputBinding = new("variable", "value") } } : step).ToArray() };
        var version = await definitions.CreateVersionAsync(definition.Id, new(null, true, false, document), default);
        var started = await new WorkflowService(store, workflowDefinitions: definitions).StartGitHubIssueWorkflowAsync(new("https://example.test/issues/1", "https://example.test/repo", null, null, WorkflowDefinitionId: definition.Id, WorkflowDefinitionVersionId: version.Id), default);
        var workflow = (await store.GetWorkflowAsync(started.WorkflowId, default))!;
        var scalar = type == "string" ? Json(value) : JsonDocument.Parse(value).RootElement.Clone();
        var source = new TaskRun { WorkflowId = workflow.Id, DefinitionStepId = "source", Kind = TaskRunKind.Custom, Status = TaskRunStatus.Succeeded, ExecutionAttemptId = Guid.NewGuid(), StructuredOutputsJson = JsonSerializer.Serialize(new Dictionary<string, JsonElement> { ["value"] = scalar }) };
        await store.UpsertTaskRunAsync(source, default);
        workflow.CurrentDefinitionStepId = "choose";
        WorkflowOrchestrator Restart() => new(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), new FakeAgentRunner(), new FilePromptRenderer());
        await Restart().AdvanceAsync(workflow, default);
        source.StructuredOutputsJson = "{}"; // A restart must reuse the committed route, without reading changed producer data.
        await Restart().AdvanceAsync(workflow, default);
        var outcome = Assert.Single(await store.ListDecisionExecutionsAsync(workflow.Id, default));
        Assert.Equal(target, outcome.SelectedTargetId); Assert.Equal(variable ? (Guid?)null : source.Id, outcome.SourceTaskRunId);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.DoesNotContain(await store.ListTaskRunsAsync(workflow.Id, default), run => run.DefinitionStepId == (target == "yes" ? "no" : "yes"));
    }
}
