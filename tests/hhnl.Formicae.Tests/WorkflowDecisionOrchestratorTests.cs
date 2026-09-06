using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Infrastructure.Prompts;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowDecisionOrchestratorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_selected_arm_runs_and_convergence_executes_once(bool selected)
    {
        var (store, workflow) = await SetupAsync(Document(Literal(selected)));
        var agent = new RecordingAgent();
        WorkflowOrchestrator Restart() => new(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new FilePromptRenderer());
        for (var tick = 0; tick < 6; tick++) await Restart().AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        var runs = await store.ListTaskRunsAsync(workflow.Id, default);
        Assert.Equal(new[] { selected ? "yes" : "no", "finish" }, runs.Select(run => run.DefinitionStepId));
        var outcome = Assert.Single(await store.ListDecisionExecutionsAsync(workflow.Id, default));
        Assert.Equal(selected, outcome.BooleanResult); Assert.Equal(selected ? "yes" : "no", outcome.SelectedTargetId);
        Assert.Equal(2, agent.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initial_decision_preserves_ready_to_plan_gate(bool parallel)
    {
        var document = Document(Literal(true));
        if (parallel) document = document with { Steps = [
            new("choose", "builtins.decision", Decision: new(Literal(true), "group", "no")),
            new("group", "builtins.parallel", "finish", Parallel: new(["a", "b"])),
            new("a", "builtins.plan", "group", NextStepPort: "join"), new("b", "builtins.plan", "group", NextStepPort: "join"),
            new("no", "builtins.plan", "finish"), new("finish", "builtins.plan") ] };
        var (store, workflow) = await SetupAsync(document); var agent = new RecordingAgent();
        var orchestrator = new WorkflowOrchestrator(store, new UnreadyIssues(), new FakeSourceControlProvider(), agent, new FilePromptRenderer());
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Queued, workflow.Status);
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(0, agent.Count);
        Assert.Empty(await store.ListTaskRunsAsync(workflow.Id, default));
    }

    [Fact]
    public async Task Retrying_initial_evaluation_error_still_requires_ready_to_plan()
    {
        var condition = new WorkflowDecisionCondition("workflowField", "string", "equals", Reference: "model", CompareTo: JsonSerializer.SerializeToElement("chosen"));
        var (store, workflow) = await SetupAsync(Document(condition)); var agent = new RecordingAgent();
        var orchestrator = new WorkflowOrchestrator(store, new UnreadyIssues(), new FakeSourceControlProvider(), agent, new FilePromptRenderer());
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status);
        workflow.Model = "chosen";
        await new WorkflowService(store).RetryWorkflowAsync(workflow.Id, default);
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Queued, workflow.Status);
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(0, agent.Count);
    }

    [Fact]
    public async Task Restart_reuses_outcome_without_reevaluating_changed_inputs_or_rewinding()
    {
        var condition = new WorkflowDecisionCondition("workflowField", "string", "equals", Reference: "model", CompareTo: JsonSerializer.SerializeToElement("chosen"));
        var (store, workflow) = await SetupAsync(Document(condition)); workflow.Model = "chosen";
        var orchestrator = new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), new RecordingAgent(), new FilePromptRenderer());
        await orchestrator.AdvanceAsync(workflow, default);
        var stale = new Workflow { Id = workflow.Id, IssueUrl = workflow.IssueUrl, RepositoryUrl = workflow.RepositoryUrl,
            WorkflowDefinitionId = workflow.WorkflowDefinitionId, WorkflowDefinitionVersionId = workflow.WorkflowDefinitionVersionId,
            CurrentDefinitionStepId = "choose", Status = WorkflowStatus.Planning, Model = null };
        await orchestrator.AdvanceAsync(workflow, default); // Real workflow has progressed through yes to finish.
        await orchestrator.AdvanceAsync(stale, default);
        Assert.Equal("finish", stale.CurrentDefinitionStepId);
        Assert.True(Assert.Single(await store.ListDecisionExecutionsAsync(workflow.Id, default)).BooleanResult);
        Assert.Equal("finish", workflow.CurrentDefinitionStepId);
    }

    [Fact]
    public async Task Missing_required_input_fails_with_node_diagnostic_and_launches_neither_arm()
    {
        var condition = new WorkflowDecisionCondition("workflowField", "string", "equals", Reference: "model", CompareTo: JsonSerializer.SerializeToElement("chosen"));
        var (store, workflow) = await SetupAsync(Document(condition)); var agent = new RecordingAgent();
        await new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new FilePromptRenderer()).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status);
        Assert.Contains("Decision 'choose'", workflow.FailureReason);
        Assert.Empty(await store.ListDecisionExecutionsAsync(workflow.Id, default)); Assert.Equal(0, agent.Count);
    }

    [Fact]
    public async Task Task_output_uses_exact_predecessor_execution_identity()
    {
        var condition = new WorkflowDecisionCondition("taskOutput", "string", "contains", Reference: "source", CompareTo: JsonSerializer.SerializeToElement("recorded"));
        var document = Document(condition); document = document with { StartStepId = "source", Steps = [new("source", "builtins.plan", "choose"), .. document.Steps] };
        var (store, workflow) = await SetupAsync(document);
        var orchestrator = new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), new RecordingAgent(), new FilePromptRenderer());
        await orchestrator.AdvanceAsync(workflow, default); await orchestrator.AdvanceAsync(workflow, default);
        var outcome = Assert.Single(await store.ListDecisionExecutionsAsync(workflow.Id, default));
        Assert.Equal(Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default)).Id, outcome.SourceTaskRunId);
        Assert.Equal("yes", outcome.SelectedTargetId);
    }

    [Fact]
    public async Task Decision_arm_can_enter_loop_and_records_configured_and_runtime_targets()
    {
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "choose", [
            new("choose", "builtins.decision", Decision: new(Literal(true), "repeat", "no")),
            new("repeat", "builtins.loop", "finish", Loop: new("body", 2, 2)),
            new("body", "builtins.plan", "repeat", NextStepPort: "return"),
            new("no", "builtins.plan", "finish"), new("finish", "builtins.plan") ]);
        var (store, workflow) = await SetupAsync(document);
        var orchestrator = new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), new RecordingAgent(), new FilePromptRenderer());
        for (var i = 0; i < 6; i++) await orchestrator.AdvanceAsync(workflow, default);
        var outcome = Assert.Single(await store.ListDecisionExecutionsAsync(workflow.Id, default));
        Assert.Equal("repeat", outcome.ConfiguredTargetId); Assert.Equal("body", outcome.SelectedTargetId);
        Assert.Equal(2, (await store.ListLoopIterationsAsync(workflow.Id, default)).Count);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
    }

    [Fact]
    public async Task Decision_arm_can_run_parallel_group_before_converging()
    {
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "choose", [
            new("choose", "builtins.decision", Decision: new(Literal(true), "parallel", "no")),
            new("parallel", "builtins.parallel", "finish", Parallel: new(["a", "b"])),
            new("a", "builtins.plan", "parallel", NextStepPort: "join"), new("b", "builtins.plan", "parallel", NextStepPort: "join"),
            new("no", "builtins.plan", "finish"), new("finish", "builtins.plan") ]);
        var (store, workflow) = await SetupAsync(document);
        var orchestrator = new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), new RecordingAgent(), new FilePromptRenderer());
        for (var i = 0; i < 6; i++) await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal(WorkflowParallelExecutionOutcome.Succeeded, (await store.GetParallelExecutionAsync(workflow.Id, "parallel", default))!.Outcome);
        Assert.DoesNotContain(await store.ListTaskRunsAsync(workflow.Id, default), run => run.DefinitionStepId == "no");
    }

    private static WorkflowDecisionCondition Literal(bool value) => new("literal", "boolean", "equals", Value: JsonSerializer.SerializeToElement(value), CompareTo: JsonSerializer.SerializeToElement(true));
    private static WorkflowDefinitionDocument Document(WorkflowDecisionCondition condition) => new(DefaultWorkflowDefinitions.V1Alpha3Schema, "choose", [
        new("choose", "builtins.decision", Decision: new(condition, "yes", "no")),
        new("yes", "builtins.plan", "finish"), new("no", "builtins.plan", "finish"), new("finish", "builtins.plan") ]);
    private static async Task<(InMemoryWorkflowStore Store, Workflow Workflow)> SetupAsync(WorkflowDefinitionDocument document)
    {
        var store = new InMemoryWorkflowStore(); var definitions = new WorkflowDefinitionService(store, new());
        var definition = await definitions.CreateAsync(new("Decision"), default);
        var version = await definitions.CreateVersionAsync(definition.Id, new(null, true, false, document), default);
        var started = await new WorkflowService(store, workflowDefinitions: definitions).StartGitHubIssueWorkflowAsync(new("https://example.test/issues/1", "https://example.test/repo", null, null, WorkflowDefinitionId: definition.Id, WorkflowDefinitionVersionId: version.Id), default);
        return (store, (await store.GetWorkflowAsync(started.WorkflowId, default))!);
    }
    private sealed class UnreadyIssues : IWorkItemProvider
    {
        public Task<WorkItem> GetIssueAsync(string url, CancellationToken token) => Task.FromResult(new WorkItem(url, "issue", "body", [], []));
        public Task<IReadOnlyList<WorkItem>> ListIssuesWithLabelAsync(string url, string label, CancellationToken token) => Task.FromResult<IReadOnlyList<WorkItem>>([]);
        public Task UpsertIssueCommentAsync(string url, string marker, string body, CancellationToken token) => Task.CompletedTask;
        public Task AddIssueCommentAsync(string url, string body, CancellationToken token) => Task.CompletedTask;
        public Task ReactToIssueAsync(string url, string reaction, CancellationToken token) => Task.CompletedTask;
        public Task ReactToIssueCommentAsync(string url, WorkItemComment comment, string reaction, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class RecordingAgent : IAgentRunner
    {
        public int Count;
        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken token)
        { var id = (++Count).ToString(); return Task.FromResult(new AgentRunStartResult(id, new(true, id, "recorded plan", null))); }
        public Task<AgentRunResult?> TryGetResultAsync(string id, CancellationToken token) => Task.FromResult<AgentRunResult?>(null);
    }
}
