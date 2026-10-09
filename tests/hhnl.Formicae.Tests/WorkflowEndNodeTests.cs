using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowEndNodeTests
{
    [Fact]
    public void End_round_trips_and_validates_as_a_manual_entry_target()
    {
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "start", [
            new("start", WorkflowStartDefinitions.Uses, "end", Event: WorkflowEventDefinitions.Configuration(new ManualEventSettings())),
            new("end", WorkflowEndDefinitions.Uses)]);
        var saved = WorkflowDefinitionJson.Deserialize(WorkflowDefinitionJson.Serialize(document));
        Assert.True(new WorkflowDefinitionValidator().Validate(saved).IsValid);
    }

    [Theory]
    [InlineData("next")]
    [InlineData("multiple")]
    [InlineData("model")]
    [InlineData("wait")]
    [InlineData("parallel")]
    [InlineData("script")]
    [InlineData("environment")]
    public void End_rejects_outgoing_connections_and_task_settings(string setting)
    {
        var end = new WorkflowDefinitionStep("end", WorkflowEndDefinitions.Uses);
        end = setting switch
        {
            "next" => end with { NextStepId = "end" },
            "multiple" => end with { NextStepIds = ["end"] },
            "model" => end with { Model = "model" },
            "wait" => end with { Wait = new(IssueNumber: 1) },
            "parallel" => end with { Parallel = new(["a", "b"]) },
            "script" => end with { Script = new("echo done") },
            _ => end with { EnvironmentId = "default" }
        };
        Assert.Contains(new WorkflowDefinitionValidator().Validate(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "end", [end])).Errors,
            error => error.Code == "definition.end.invalid");
    }

    [Fact]
    public async Task Sequential_end_completes_without_starting_a_worker_and_is_idempotent()
    {
        var (store, workflow, agent) = await Setup(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "end", [new("end", WorkflowEndDefinitions.Uses)]));
        await Restart(store, agent).AdvanceAsync(workflow, default);
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal(WorkflowStep.Done, workflow.CurrentStep);
        var end = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default));
        Assert.Equal(TaskRunKind.End, end.Kind); Assert.Equal(TaskRunStatus.Succeeded, end.Status);
        Assert.Null(end.ExternalId); Assert.Empty(agent.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task First_arrival_stops_running_queued_and_waiting_siblings_across_restart(bool cycle)
    {
        var (store, workflow, agent) = await Setup(Graph(cycle));
        await Succeed(store, workflow, "entry");
        await Succeed(store, workflow, "fast");
        var running = await store.UpsertTaskRunAsync(new() { WorkflowId = workflow.Id, DefinitionStepId = "slow", Kind = TaskRunKind.Plan,
            Status = TaskRunStatus.Running, ExecutionAttemptId = Guid.NewGuid(), ExternalId = "slow-worker" }, default);
        var waiting = await store.UpsertTaskRunAsync(new() { WorkflowId = workflow.Id, DefinitionStepId = "wait", Kind = TaskRunKind.Wait,
            Status = TaskRunStatus.Waiting, ExecutionAttemptId = Guid.NewGuid() }, default);
        var queued = await store.UpsertTaskRunAsync(new() { WorkflowId = workflow.Id, DefinitionStepId = "queued", Kind = TaskRunKind.Plan }, default);
        var wait = await store.ArmWaitAsync(new() { WorkflowId = workflow.Id, TaskRunId = waiting.Id, ExecutionAttemptId = waiting.ExecutionAttemptId!.Value,
            Uses = "github.issue-commented", Provider = "GitHub", RepositoryUrl = "repo", IssueUrl = "issue", ArmedAt = DateTimeOffset.UtcNow }, default);
        if (cycle)
        {
            var state = new WorkflowCycleState { Entry = "entry" };
            state.Completed["entry"] = 1; state.Completed["fast"] = 1;
            state.Delivered[System.Text.Json.JsonSerializer.Serialize(new[] { "fast", "end" })] = 1;
            WorkflowCycleDefinitions.Save(workflow, state);
        }
        agent.FailCancel = true;
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.True(wait.IsCanceled); Assert.Equal(TaskRunStatus.Canceled, waiting.Status); Assert.Equal(TaskRunStatus.Canceled, queued.Status);
        Assert.Equal(TaskRunStatus.Running, running.Status); Assert.Empty(agent.Starts);
        Assert.Contains(await store.ListRunnableWorkflowsAsync(default), item => item.Id == workflow.Id);
        agent.FailCancel = false;
        await Restart(store, agent).AdvanceRunnableWorkflowsAsync(default);
        Assert.Equal(TaskRunStatus.Canceled, running.Status); Assert.False(running.RuntimeCleanupPending);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status); Assert.Null(workflow.CancelRequestedAt);
        Assert.Equal("slow-worker", agent.Canceled[^1]); Assert.Empty(agent.Starts);
        Assert.DoesNotContain(await store.ListRunnableWorkflowsAsync(default), item => item.Id == workflow.Id);
        if (!cycle) Assert.Equal(WorkflowParallelExecutionOutcome.Succeeded, Assert.Single(await store.ListParallelExecutionsAsync(workflow.Id, default)).Outcome);
    }

    [Fact]
    public async Task Persisted_end_arrival_recovers_before_launching_other_graph_work()
    {
        var (store, workflow, agent) = await Setup(Graph());
        await Succeed(store, workflow, "end", TaskRunKind.End);
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status); Assert.Empty(agent.Starts);
    }

    [Fact]
    public async Task Explicit_parallel_branch_end_cancels_other_branch_and_finalizes_activation()
    {
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "fork", [
            new("fork", WorkflowParallelDefinitions.Uses, "after", Parallel: new(["a", "b"])),
            new("a", "builtins.plan", "end"), new("end", WorkflowEndDefinitions.Uses),
            new("b", "builtins.plan", "fork", NextStepPort: "join"), new("after", "builtins.plan")]);
        Assert.True(new WorkflowDefinitionValidator().Validate(document).IsValid);
        var (store, workflow, agent) = await Setup(document);
        await Succeed(store, workflow, "a");
        var sibling = await store.UpsertTaskRunAsync(new() { WorkflowId = workflow.Id, DefinitionStepId = "b", Kind = TaskRunKind.Plan,
            Status = TaskRunStatus.Running, ExternalId = "branch-b", ExecutionAttemptId = Guid.NewGuid() }, default);
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status); Assert.Equal(TaskRunStatus.Canceled, sibling.Status);
        Assert.Equal(WorkflowParallelExecutionOutcome.Canceled, Assert.Single(await store.ListParallelExecutionsAsync(workflow.Id, default)).Outcome);
        Assert.Empty(agent.Starts);
        Assert.DoesNotContain(await store.ListTaskRunsAsync(workflow.Id, default), run => run.DefinitionStepId == "after");
    }

    [Fact]
    public void Shared_end_does_not_require_an_inactive_entry_to_join()
    {
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "entry", [
            new("entry", "builtins.plan", "end", NextStepIds: ["slow"]), new("slow", "builtins.plan"),
            new("event", WorkflowNodeDefinitions.TriggerUses, "other", Trigger: new(WorkflowTriggerType.Manual, false, [], null)),
            new("other", "builtins.plan", "end"), new("end", WorkflowEndDefinitions.Uses)]);
        Assert.True(new WorkflowDefinitionValidator().Validate(document).IsValid);
    }

    [Fact]
    public async Task Wait_cleanup_store_failure_does_not_downgrade_successful_end_arrival()
    {
        var (store, workflow, agent) = await Setup(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "end", [new("end", WorkflowEndDefinitions.Uses)]));
        var proxy = System.Reflection.DispatchProxy.Create<IWorkflowStore, FailWaitCleanupStore>();
        ((FailWaitCleanupStore)(object)proxy).Inner = store;
        await Restart(proxy, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal(TaskRunStatus.Succeeded, Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default)).Status);
        Assert.Contains(await store.ListLogsAsync(workflow.Id, default), log => log.Message.Contains("wait cleanup unavailable"));
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status); Assert.Empty(agent.Starts);
    }

    public class FailWaitCleanupStore : System.Reflection.DispatchProxy
    {
        public IWorkflowStore Inner = null!;
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
            => method!.Name == nameof(IWorkflowStore.CancelWaitsAsync)
                ? Task.FromException(new IOException("wait cleanup unavailable")) : method.Invoke(Inner, args);
    }

    private static WorkflowDefinitionDocument Graph(bool cycle = false) => new(DefaultWorkflowDefinitions.V1Alpha3Schema, "entry", [
        new("entry", "builtins.plan", "fast", NextStepIds: ["slow", "wait", "queued"]),
        new("fast", "builtins.plan", "end", NextStepIds: cycle ? ["fast"] : null),
        new("slow", "builtins.plan", "end"), new("wait", "github.issue-commented", Wait: new(IssueNumber: 1)),
        new("queued", "builtins.plan"), new("end", WorkflowEndDefinitions.Uses)]);

    private static async Task Succeed(InMemoryWorkflowStore store, Workflow workflow, string id, TaskRunKind kind = TaskRunKind.Plan)
        => await store.UpsertTaskRunAsync(new() { WorkflowId = workflow.Id, DefinitionStepId = id, Kind = kind,
            Status = TaskRunStatus.Succeeded, Output = "plan", ExecutionAttemptId = Guid.NewGuid() }, default);

    private static async Task<(InMemoryWorkflowStore, Workflow, Agent)> Setup(WorkflowDefinitionDocument document)
    {
        Assert.True(new WorkflowDefinitionValidator().Validate(document).IsValid);
        var store = new InMemoryWorkflowStore();
        var definition = await store.CreateWorkflowDefinitionAsync(new() { Name = "End test" }, default);
        var version = await store.CreateWorkflowDefinitionVersionAsync(new() { WorkflowDefinitionId = definition.Id, Version = 1,
            DslSchemaVersion = document.Schema, DefinitionJson = WorkflowDefinitionJson.Serialize(document), IsEnabled = true }, default);
        var workflow = await store.CreateWorkflowAsync(new() { IssueUrl = "issue", RepositoryUrl = "repo", Status = WorkflowStatus.Running,
            CurrentDefinitionStepId = document.StartStepId, WorkflowDefinitionId = definition.Id, WorkflowDefinitionVersionId = version.Id }, default);
        return (store, workflow, new());
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
        public List<AgentTask> Starts = []; public List<string> Canceled = []; public bool FailCancel;
        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken token)
        { Starts.Add(task); return Task.FromResult(new AgentRunStartResult("unexpected")); }
        public Task<AgentRunResult?> TryGetResultAsync(string id, CancellationToken token) => Task.FromResult<AgentRunResult?>(null);
        public Task CancelAsync(string id, CancellationToken token)
        { Canceled.Add(id); if (FailCancel) throw new IOException("cancel unavailable"); return Task.CompletedTask; }
    }
}
