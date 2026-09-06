using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowParallelOrchestratorTests
{
    [Fact]
    public async Task Starts_independent_jobs_together_and_joins_in_definition_order_after_restart()
    {
        var (store, workflow) = await SetupAsync();
        var agent = new DeferredAgent(); var issues = new CountingIssues();
        WorkflowOrchestrator Restart() => new(store, issues, new FakeSourceControlProvider(), agent, new SnapshotPrompt());
        await Restart().AdvanceAsync(workflow, default);
        Assert.Equal(2, agent.Tasks.Count);
        Assert.All(agent.Tasks, task => Assert.NotNull(task.ExecutionAttemptId));
        var runs = await store.ListTaskRunsAsync(workflow.Id, default);
        agent.Complete(runs.Single(run => run.DefinitionStepId == "b"), "B output");
        await Restart().AdvanceAsync(workflow, default);
        Assert.Equal("parallel", workflow.CurrentDefinitionStepId);
        Assert.Empty(issues.IssueComments);
        agent.Complete(runs.Single(run => run.DefinitionStepId == "a"), "A output");
        await Restart().AdvanceAsync(workflow, default);
        Assert.Equal("finish", workflow.CurrentDefinitionStepId);
        Assert.Equal(2, agent.Tasks.Count);
        Assert.True(workflow.PlanArtifact!.IndexOf("A output", StringComparison.Ordinal) < workflow.PlanArtifact.IndexOf("B output", StringComparison.Ordinal));
        Assert.Single(issues.IssueComments);
        Assert.Equal(WorkflowParallelExecutionOutcome.Succeeded, (await store.GetParallelExecutionAsync(workflow.Id, "parallel", default))!.Outcome);
        // Recover the window after activation completion and before cursor persistence.
        workflow.CurrentDefinitionStepId = "parallel";
        await Restart().AdvanceAsync(workflow, default);
        Assert.Equal(2, agent.Tasks.Count);
        Assert.Single(issues.IssueComments);
        Assert.Equal(1, issues.Upserts);
        Assert.Equal("finish", workflow.CurrentDefinitionStepId);
    }

    [Fact]
    public async Task Failed_branch_stops_its_suffix_while_unaffected_branch_finishes()
    {
        var (store, workflow) = await SetupAsync(chains: true);
        var agent = new DeferredAgent();
        var orchestrator = new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new SnapshotPrompt());
        await orchestrator.AdvanceAsync(workflow, default);
        var runs = await store.ListTaskRunsAsync(workflow.Id, default);
        agent.Complete(runs.Single(run => run.DefinitionStepId == "a"), "", false);
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.NotEqual(WorkflowStatus.Failed, workflow.Status);
        agent.Complete(runs.Single(run => run.DefinitionStepId == "b"), "B first");
        await orchestrator.AdvanceAsync(workflow, default);
        await orchestrator.AdvanceAsync(workflow, default);
        var b2 = (await store.ListTaskRunsAsync(workflow.Id, default)).Single(run => run.DefinitionStepId == "b2");
        Assert.DoesNotContain(await store.ListTaskRunsAsync(workflow.Id, default), run => run.DefinitionStepId == "a2");
        Assert.Contains(agent.Tasks, task => task.Prompt == "b2:B first");
        agent.Complete(b2, "B second");
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status);
        Assert.Contains("branch 1, task 'a'", workflow.FailureReason);
        Assert.Equal(WorkflowParallelExecutionOutcome.Failed, (await store.GetParallelExecutionAsync(workflow.Id, "parallel", default))!.Outcome);
    }

    [Fact]
    public async Task Retry_preserves_successful_branch_and_entry_snapshot()
    {
        var (store, workflow) = await SetupAsync(); var agent = new DeferredAgent();
        WorkflowOrchestrator Restart() => new(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new SnapshotPrompt());
        await Restart().AdvanceAsync(workflow, default);
        var runs = await store.ListTaskRunsAsync(workflow.Id, default);
        var a = runs.Single(run => run.DefinitionStepId == "a"); var b = runs.Single(run => run.DefinitionStepId == "b");
        agent.Complete(a, "", false); agent.Complete(b, "B success");
        await Restart().AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status);
        // The retry service owns this reset; exercise scheduler recovery from its durable state.
        a.Status = TaskRunStatus.Queued; a.ExternalId = null; a.ExecutionAttemptId = null; a.FailureReason = null;
        await store.UpsertTaskRunAsync(a, default);
        workflow.Status = WorkflowStatus.Planning; workflow.PlanArtifact = "mutated after activation";
        await Restart().AdvanceAsync(workflow, default);
        Assert.Equal(3, agent.Tasks.Count);
        Assert.Equal("a:entry snapshot", agent.Tasks[^1].Prompt);
        agent.Complete(a, "A retry");
        await Restart().AdvanceAsync(workflow, default);
        Assert.Equal("finish", workflow.CurrentDefinitionStepId);
        Assert.Contains("B success", workflow.PlanArtifact);
    }

    [Fact]
    public async Task Uncertain_launch_reuses_attempt_and_does_not_block_other_branch()
    {
        var (store, workflow) = await SetupAsync(); var agent = new DeferredAgent { LoseFirstResponse = true };
        WorkflowOrchestrator Restart() => new(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new SnapshotPrompt());
        await Restart().AdvanceAsync(workflow, default);
        Assert.Equal(2, agent.Tasks.Count);
        var attempt = agent.Tasks[0].ExecutionAttemptId;
        var a = (await store.ListTaskRunsAsync(workflow.Id, default)).Single(run => run.DefinitionStepId == "a");
        Assert.Equal(TaskRunStatus.Running, a.Status); Assert.Null(a.ExternalId);
        await Restart().AdvanceAsync(workflow, default);
        Assert.Equal(attempt, agent.Tasks[^1].ExecutionAttemptId);
        Assert.Equal(2, agent.Jobs.Count);
        Assert.NotNull(a.ExternalId);
    }

    [Fact]
    public async Task Immediate_success_is_not_regressed_when_output_logging_fails()
    {
        var (store, workflow) = await SetupAsync(); var agent = new DeferredAgent { ImmediateResult = true };
        var proxy = System.Reflection.DispatchProxy.Create<IWorkflowStore, StoreFaultProxy>();
        var fault = (StoreFaultProxy)(object)proxy; fault.Inner = store; fault.FailAssignment = false;
        var orchestrator = new WorkflowOrchestrator(proxy, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new SnapshotPrompt());
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(2, agent.Tasks.Count);
        Assert.All(await store.ListTaskRunsAsync(workflow.Id, default), run => Assert.Equal(TaskRunStatus.Succeeded, run.Status));
        Assert.Equal("finish", workflow.CurrentDefinitionStepId);
    }

    [Fact]
    public async Task Accepted_launch_bookkeeping_failure_keeps_siblings_runnable_even_when_logging_fails()
    {
        var (store, workflow) = await SetupAsync(); var agent = new DeferredAgent();
        var proxy = System.Reflection.DispatchProxy.Create<IWorkflowStore, StoreFaultProxy>();
        var fault = (StoreFaultProxy)(object)proxy; fault.Inner = store;
        var orchestrator = new WorkflowOrchestrator(proxy, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new SnapshotPrompt());
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(2, agent.Tasks.Count);
        Assert.Equal(WorkflowStatus.Planning, workflow.Status);
        Assert.All(await store.ListTaskRunsAsync(workflow.Id, default), run => Assert.Equal(TaskRunStatus.Running, run.Status));
        foreach (var run in await store.ListTaskRunsAsync(workflow.Id, default)) agent.Complete(run, "recovered");
        fault.FailLogs = false;
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal("finish", workflow.CurrentDefinitionStepId);
        Assert.Equal(2, agent.Tasks.Count);
    }

    [Fact]
    public async Task Status_read_error_keeps_running_job_tracked_until_recovery()
    {
        var (store, workflow) = await SetupAsync(); var agent = new DeferredAgent();
        var orchestrator = new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new SnapshotPrompt());
        await orchestrator.AdvanceAsync(workflow, default);
        agent.FailPoll = true;
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.All(await store.ListTaskRunsAsync(workflow.Id, default), run => Assert.Equal(TaskRunStatus.Running, run.Status));
        Assert.Equal(WorkflowStatus.Planning, workflow.Status);
        agent.FailPoll = false;
        foreach (var run in await store.ListTaskRunsAsync(workflow.Id, default)) agent.Complete(run, "recovered");
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal("finish", workflow.CurrentDefinitionStepId);
        Assert.Equal(2, agent.Tasks.Count);
    }

    [Fact]
    public async Task Feedback_after_parallel_join_cannot_rewind_into_branch()
    {
        var (store, workflow) = await SetupAsync(implementation: true); var agent = new DeferredAgent();
        var issues = new CountingIssues();
        var orchestrator = new WorkflowOrchestrator(store, issues, new FakeSourceControlProvider(), agent, new SnapshotPrompt());
        await orchestrator.AdvanceAsync(workflow, default);
        foreach (var run in await store.ListTaskRunsAsync(workflow.Id, default)) agent.Complete(run, "plan");
        await orchestrator.AdvanceAsync(workflow, default);
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal("finish", workflow.CurrentDefinitionStepId);
        Assert.Equal(TaskRunKind.Implement, agent.Tasks[^1].Kind);
    }

    [Fact]
    public async Task Caller_cancellation_keeps_workflow_resumable()
    {
        var (store, workflow) = await SetupAsync(); var agent = new DeferredAgent();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var orchestrator = new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new SnapshotPrompt());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.AdvanceAsync(workflow, cancellation.Token));
        Assert.NotEqual(WorkflowStatus.Failed, workflow.Status);
    }

    private static async Task<(InMemoryWorkflowStore Store, Workflow Workflow)> SetupAsync(bool chains = false, bool implementation = false)
    {
        var store = new InMemoryWorkflowStore(); var definitions = new WorkflowDefinitionService(store, new());
        var definition = await definitions.CreateAsync(new("parallel"), default);
        var steps = new List<WorkflowDefinitionStep> {
            new("parallel", WorkflowParallelDefinitions.Uses, "finish", Parallel: new(["a", "b"])),
            new("a", "builtins.plan", chains ? "a2" : "parallel", NextStepPort: chains ? null : "join"),
            new("b", "builtins.plan", chains ? "b2" : "parallel", NextStepPort: chains ? null : "join"),
            new("finish", implementation ? "builtins.implement" : "builtins.plan") };
        if (chains) { steps.Add(new("a2", "builtins.plan", "parallel", NextStepPort: "join")); steps.Add(new("b2", "builtins.plan", "parallel", NextStepPort: "join")); }
        var version = await definitions.CreateVersionAsync(definition.Id, new(null, true, false, new(DefaultWorkflowDefinitions.V1Alpha3Schema, "parallel", steps)), default);
        var service = new WorkflowService(store, workflowDefinitions: definitions);
        var started = await service.StartGitHubIssueWorkflowAsync(new("https://example.test/issues/1", "https://example.test/repo", null, null, WorkflowDefinitionId: definition.Id, WorkflowDefinitionVersionId: version.Id), default);
        var workflow = (await store.GetWorkflowAsync(started.WorkflowId, default))!; workflow.PlanArtifact = "entry snapshot";
        return (store, workflow);
    }

    private sealed class SnapshotPrompt : IPromptRenderer
    {
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? item, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult($"{workflow.CurrentDefinitionStepId}:{workflow.PlanArtifact}"); }
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? item, IReadOnlyList<PullRequestComment> comments, CancellationToken token)
            => RenderAsync(kind, workflow, item, token);
    }
    public class StoreFaultProxy : System.Reflection.DispatchProxy
    {
        public IWorkflowStore Inner = null!;
        public bool FailLogs = true;
        public bool FailAssignment = true;
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowStore.UpsertTaskRunAsync) && args![0] is TaskRun { ExternalId: not null, Status: TaskRunStatus.Running } && FailAssignment)
            { FailAssignment = false; throw new InvalidOperationException("Assignment persistence unavailable"); }
            if (method.Name == nameof(IWorkflowStore.AddLogAsync) && FailLogs) throw new InvalidOperationException("Log persistence unavailable");
            try { return method.Invoke(Inner, args); }
            catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
        }
    }

    private sealed class CountingIssues : IWorkItemProvider
    {
        private readonly FakeWorkItemProvider inner = new();
        public int Upserts;
        public List<string> IssueComments => inner.IssueComments;
        public async Task<WorkItem> GetIssueAsync(string url, CancellationToken token)
        {
            var issue = await inner.GetIssueAsync(url, token);
            return issue with { Comments = [new WorkItemComment("feedback", "user", "Please revise", url, DateTimeOffset.UtcNow.AddMinutes(1))] };
        }
        public Task<IReadOnlyList<WorkItem>> ListIssuesWithLabelAsync(string url, string label, CancellationToken token) => inner.ListIssuesWithLabelAsync(url, label, token);
        public Task UpsertIssueCommentAsync(string url, string marker, string body, CancellationToken token)
        { Upserts++; return inner.UpsertIssueCommentAsync(url, marker, body, token); }
        public Task AddIssueCommentAsync(string url, string body, CancellationToken token) => inner.AddIssueCommentAsync(url, body, token);
        public Task ReactToIssueAsync(string url, string reaction, CancellationToken token) => Task.CompletedTask;
        public Task ReactToIssueCommentAsync(string url, WorkItemComment comment, string reaction, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class DeferredAgent : IAgentRunner
    {
        public List<AgentTask> Tasks { get; } = [];
        public HashSet<Guid> Jobs { get; } = [];
        private readonly Dictionary<string, AgentRunResult> results = [];
        public bool LoseFirstResponse;
        public bool FailPoll;
        public bool ImmediateResult;
        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken token)
        {
            Tasks.Add(task); var identity = task.ExecutionAttemptId ?? Guid.NewGuid(); Jobs.Add(identity);
            if (LoseFirstResponse) { LoseFirstResponse = false; throw new HttpRequestException("response lost"); }
            return Task.FromResult(new AgentRunStartResult(identity.ToString("N"), ImmediateResult ? new(true, identity.ToString("N"), "immediate plan", null) : null));
        }
        public Task<AgentRunResult?> TryGetResultAsync(string id, CancellationToken token)
        { if (FailPoll) throw new InvalidOperationException("status service failed"); return Task.FromResult(results.GetValueOrDefault(id)); }
        public void Complete(TaskRun run, string output, bool success = true) => results[run.ExternalId!] = new(success, run.ExternalId!, output, success ? null : "branch failed");
    }
}
