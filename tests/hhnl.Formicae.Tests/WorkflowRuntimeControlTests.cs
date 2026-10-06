using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowRuntimeControlTests
{
    [Fact]
    public async Task Pause_records_running_completion_and_resume_launches_the_next_step()
    {
        var (store, workflow, agent) = await Setup();
        await Restart(store, agent).AdvanceAsync(workflow, default);
        var run = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default));
        Assert.NotNull(run.ExecutionAttemptId); Assert.Equal(run.ExecutionAttemptId, agent.Starts[0].ExecutionAttemptId);
        var settingsEvent = Assert.Single(await store.ListEventsAsync(workflow.Id, default), item => item.Type == "AgentSettingsResolved");
        using var settings = System.Text.Json.JsonDocument.Parse(settingsEvent.DetailsJson!);
        Assert.Equal(run.ExecutionAttemptId, settings.RootElement.GetProperty("executionAttemptId").GetGuid());
        Assert.Equal(run.ExternalId, settings.RootElement.GetProperty("externalId").GetString());
        workflow.IsPaused = true; agent.Result = new(true, run.ExternalId!, "the plan", null);
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(TaskRunStatus.Succeeded, run.Status); Assert.True(run.RuntimeLogsCaptured);
        Assert.False(run.RuntimeCleanupPending); Assert.Single(agent.Starts);
        var attemptLogs = await store.QueryLogsAsync(workflow.Id,
            new WorkflowLogQuery(TaskRunId: run.Id, ExecutionAttemptId: run.ExecutionAttemptId), default);
        Assert.Contains(attemptLogs.Items, log => log.Source == "system" && log.Message == "the plan"
            && log.ExternalId == run.ExternalId);
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Single(agent.Starts);
        workflow.IsPaused = false; agent.Result = null;
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(2, agent.Starts.Count);
    }

    [Fact]
    public async Task Log_capture_failure_preserves_job_and_retries_existing_attempt_after_restart()
    {
        var (store, workflow, agent) = await Setup();
        await Restart(store, agent).AdvanceAsync(workflow, default);
        var run = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default));
        agent.Result = new(true, run.ExternalId!, "the plan", null); agent.FailLogs = true;
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(TaskRunStatus.Running, run.Status); Assert.Empty(agent.Acknowledged);
        Assert.NotEqual(WorkflowStatus.Failed, workflow.Status);
        agent.FailLogs = false;
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(TaskRunStatus.Succeeded, run.Status); Assert.Single(agent.Acknowledged); Assert.Single(agent.Starts);
        Assert.Contains(await store.ListLogsAsync(workflow.Id, default), log => log.Message == "worker output");
    }

    [Fact]
    public async Task Cleanup_failure_retains_durable_pending_flag_and_terminal_workflow_reconciles_after_restart()
    {
        var (store, workflow, agent) = await Setup();
        await Restart(store, agent).AdvanceAsync(workflow, default);
        var run = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default));
        agent.Result = new(false, run.ExternalId!, "failed", "failure"); agent.FailCleanup = true;
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status); Assert.True(run.RuntimeCleanupPending);
        agent.FailCleanup = false;
        await Restart(store, agent).AdvanceRunnableWorkflowsAsync(default);
        Assert.False(run.RuntimeCleanupPending); Assert.True(run.RuntimeLogsCaptured);
        Assert.Single(agent.Starts);
    }

    [Fact]
    public async Task Cancellation_reconciles_crash_before_external_assignment_and_does_not_launch()
    {
        var (store, workflow, agent) = await Setup();
        var attempt = Guid.NewGuid();
        var run = await store.UpsertTaskRunAsync(new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.Plan,
            DefinitionStepId = "plan", Status = TaskRunStatus.Running, ExecutionAttemptId = attempt, RuntimeCleanupPending = true }, default);
        workflow.CancelRequestedAt = DateTimeOffset.UtcNow; agent.FailCancel = true;
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Null(workflow.CancelCompletedAt); Assert.Equal(TaskRunStatus.Running, run.Status);
        Assert.True(run.RuntimeLogsCaptured); Assert.Empty(agent.Starts);
        agent.FailCancel = false;
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Canceled, workflow.Status); Assert.NotNull(workflow.CancelCompletedAt);
        Assert.Equal(TaskRunStatus.Canceled, run.Status); Assert.False(run.RuntimeCleanupPending);
        Assert.All(agent.Canceled, id => Assert.Equal("job-" + attempt, id));
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(2, agent.Canceled.Count); Assert.Empty(agent.Starts);
    }

    [Fact]
    public async Task Already_canceled_workflow_with_running_worker_is_reconciled()
    {
        var (store, workflow, agent) = await Setup();
        await Restart(store, agent).AdvanceAsync(workflow, default);
        workflow.Status = WorkflowStatus.Canceled;
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Single(agent.Canceled); Assert.NotNull(workflow.CancelRequestedAt); Assert.NotNull(workflow.CancelCompletedAt);
    }

    [Fact]
    public async Task Automatic_failed_workflow_cleanup_preserves_retryability_without_user_cancel_intent()
    {
        var (store, workflow, agent) = await Setup();
        await Restart(store, agent).AdvanceAsync(workflow, default);
        workflow.Status = WorkflowStatus.Failed; workflow.FailureReason = "orchestration failed";
        agent.FailCancel = true;
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Null(workflow.CancelRequestedAt); Assert.Null(workflow.CancelCompletedAt);
        agent.FailCancel = false;
        await Restart(store, agent).AdvanceAsync(workflow, default);
        var run = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default));
        Assert.Equal(TaskRunStatus.Failed, run.Status); Assert.False(run.RuntimeCleanupPending);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status); Assert.Equal("plan", workflow.CurrentDefinitionStepId);
        Assert.Null(workflow.CancelRequestedAt); Assert.Null(workflow.CancelCompletedAt);
    }

    [Fact]
    public async Task Paused_parallel_workflow_records_active_branch_without_starting_waiting_branch()
    {
        var store = new InMemoryWorkflowStore();
        var definition = await store.CreateWorkflowDefinitionAsync(new WorkflowDefinition { Name = "parallel" }, default);
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "fork", [
            new("fork", "builtins.parallel", "finish", Parallel: new(["a", "b"])),
            new("a", "builtins.plan", "fork", NextStepPort: "join"),
            new("b", "builtins.plan", "fork", NextStepPort: "join"),
            new("finish", "builtins.implement")]);
        var version = await store.CreateWorkflowDefinitionVersionAsync(new WorkflowDefinitionVersion { WorkflowDefinitionId = definition.Id,
            Version = 1, DslSchemaVersion = document.Schema, DefinitionJson = WorkflowDefinitionJson.Serialize(document), IsEnabled = true }, default);
        var workflow = await store.CreateWorkflowAsync(new Workflow { IssueUrl = "issue", RepositoryUrl = "repo", Status = WorkflowStatus.Planning,
            IsPaused = true, WorkflowDefinitionId = definition.Id, WorkflowDefinitionVersionId = version.Id, CurrentDefinitionStepId = "fork", CurrentStep = WorkflowStep.Plan }, default);
        var run = await store.UpsertTaskRunAsync(new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.Plan, DefinitionStepId = "a",
            Status = TaskRunStatus.Running, ExternalId = "job-a", ExecutionAttemptId = Guid.NewGuid(), RuntimeCleanupPending = true }, default);
        var agent = new Agent { Result = new(true, "job-a", "branch result", null) };
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(TaskRunStatus.Succeeded, run.Status); Assert.Empty(agent.Starts);
        Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default)); Assert.Equal("fork", workflow.CurrentDefinitionStepId);
    }

    [Fact]
    public async Task Paused_custom_task_reattaches_uncertain_identity_and_records_completion_without_launching()
    {
        var store = new InMemoryWorkflowStore();
        var definition = await store.CreateWorkflowDefinitionAsync(new WorkflowDefinition { Name = "paused custom recovery" }, default);
        var snapshot = new CustomTaskSnapshot("inspect", 1, "Inspect", "", "Inspect repository", [], new());
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "custom", [
            new("custom", CustomTaskDefinitions.Uses, "next", CustomTask: new("inspect", Snapshot: snapshot)),
            new("next", "builtins.plan")]);
        var version = await store.CreateWorkflowDefinitionVersionAsync(new WorkflowDefinitionVersion { WorkflowDefinitionId = definition.Id,
            Version = 1, DslSchemaVersion = document.Schema, DefinitionJson = WorkflowDefinitionJson.Serialize(document), IsEnabled = true }, default);
        var workflow = await store.CreateWorkflowAsync(new Workflow { IssueUrl = "issue", RepositoryUrl = "repo", Status = WorkflowStatus.Running,
            IsPaused = true, WorkflowDefinitionId = definition.Id, WorkflowDefinitionVersionId = version.Id,
            CurrentDefinitionStepId = "custom", CurrentStep = WorkflowStep.Custom }, default);
        var attempt = Guid.NewGuid(); var expectedJob = "job-" + attempt;
        var run = await store.UpsertTaskRunAsync(new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.Custom,
            DefinitionStepId = "custom", Status = TaskRunStatus.Running, ExecutionAttemptId = attempt, RuntimeCleanupPending = true }, default);
        var agent = new Agent();

        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Empty(agent.Starts); Assert.Equal(expectedJob, run.ExternalId);
        Assert.Equal(expectedJob, Assert.Single(agent.Polled)); Assert.Equal(TaskRunStatus.Running, run.Status);

        agent.Result = new(true, expectedJob, "inspection complete", null);
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Empty(agent.Starts); Assert.Equal(attempt, run.ExecutionAttemptId);
        Assert.Equal(TaskRunStatus.Succeeded, run.Status); Assert.Equal("inspection complete", run.Output);
        Assert.True(run.RuntimeLogsCaptured); Assert.False(run.RuntimeCleanupPending);
        Assert.Equal(expectedJob, Assert.Single(agent.Acknowledged));
        Assert.Contains(await store.ListLogsAsync(workflow.Id, default), log => log.Message == "worker output"
            && log.TaskRunId == run.Id && log.ExecutionAttemptId == attempt && log.ExternalId == expectedJob);
        Assert.True(workflow.IsPaused); Assert.Equal("next", workflow.CurrentDefinitionStepId);
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Empty(agent.Starts); Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default));
    }

    private static async Task<(InMemoryWorkflowStore, Workflow, Agent)> Setup()
    {
        var store = new InMemoryWorkflowStore(); var (definition, version) = DefaultWorkflowDefinitions.CreateMvp();
        await store.EnsureDefaultWorkflowDefinitionAsync(definition, version, default);
        var workflow = await store.CreateWorkflowAsync(new Workflow { IssueUrl = "https://example.test/issues/1", RepositoryUrl = "repo",
            WorkflowDefinitionId = definition.Id, WorkflowDefinitionVersionId = version.Id, CurrentDefinitionStepId = "plan" }, default);
        return (store, workflow, new Agent());
    }
    private static WorkflowOrchestrator Restart(IWorkflowStore store, Agent agent)
        => new(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new Prompt());
    private sealed class Prompt : IPromptRenderer
    {
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? item, CancellationToken token) => Task.FromResult("prompt");
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? item, IReadOnlyList<PullRequestComment> comments, CancellationToken token) => Task.FromResult("prompt");
    }
    private sealed class Agent : IAgentRunner
    {
        public List<AgentTask> Starts = []; public List<string> Acknowledged = []; public List<string> Canceled = []; public List<string> Polled = [];
        public AgentRunResult? Result; public bool FailLogs; public bool FailCleanup; public bool FailCancel;
        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken token)
        { Starts.Add(task); return Task.FromResult(new AgentRunStartResult("job-" + task.ExecutionAttemptId)); }
        public Task<AgentRunResult?> TryGetResultAsync(string id, CancellationToken token)
        { Polled.Add(id); return Task.FromResult(Result); }
        public Task<IReadOnlyList<AgentRuntimeLog>> ReadLogsAsync(string id, CancellationToken token)
            => FailLogs ? Task.FromException<IReadOnlyList<AgentRuntimeLog>>(new IOException("logs unavailable"))
                : Task.FromResult<IReadOnlyList<AgentRuntimeLog>>([new("stdout", "worker output", DateTimeOffset.UtcNow, Guid.Parse("11111111-0000-0000-0000-000000000001"))]);
        public Task AcknowledgeCompletionAsync(string id, CancellationToken token)
        { if (FailCleanup) throw new IOException("cleanup unavailable"); Acknowledged.Add(id); return Task.CompletedTask; }
        public Task CancelAsync(string id, CancellationToken token)
        { Canceled.Add(id); if (FailCancel) throw new IOException("cancel unavailable"); return Task.CompletedTask; }
        public string? ResolveExternalId(Guid workflowId, TaskRunKind kind, Guid? attemptId) => "job-" + attemptId;
    }
}
