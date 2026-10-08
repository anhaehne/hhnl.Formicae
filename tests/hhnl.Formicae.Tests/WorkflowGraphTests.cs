using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowGraphTests
{
    private static WorkflowDefinitionDocument Diamond(bool scripts = false) => new(DefaultWorkflowDefinitions.V1Alpha3Schema, "start", [
        new("start", "builtins.plan", "a", NextStepIds: ["b"]),
        new("a", scripts ? "builtins.script" : "builtins.plan", "join", Script: scripts ? new("echo a") : null),
        new("b", scripts ? "builtins.script" : "builtins.plan", "join", Script: scripts ? new("echo b") : null),
        new("join", scripts ? "builtins.script" : "builtins.plan", Script: scripts ? new("echo joined") : null)
    ]);

    [Fact]
    public void Multiple_connections_round_trip_and_validate()
    {
        var saved = WorkflowDefinitionJson.Deserialize(WorkflowDefinitionJson.Serialize(Diamond()))!;
        Assert.True(new WorkflowDefinitionValidator().Validate(saved).IsValid);
        Assert.Equal(["a", "b"], WorkflowGraphDefinitions.Successors(saved.Steps[0]));
        Assert.Equal(["b"], saved.Steps[0].NextStepIds);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("cycle")]
    [InlineData("blank")]
    public void Invalid_connections_are_rejected(string problem)
    {
        var document = Diamond();
        var steps = document.Steps.ToArray();
        if (problem == "missing") steps[0] = steps[0] with { NextStepIds = ["missing"] };
        if (problem == "duplicate") steps[0] = steps[0] with { NextStepIds = ["a"] };
        if (problem == "cycle") steps[3] = steps[3] with { NextStepId = "start" };
        if (problem == "blank") steps[0] = steps[0] with { NextStepIds = [""] };
        Assert.False(new WorkflowDefinitionValidator().Validate(document with { Steps = steps }).IsValid);
    }

    [Fact]
    public void A_join_cannot_depend_on_an_inactive_entry()
    {
        var document = Diamond();
        document = document with { Steps = [..document.Steps,
            new("trigger", WorkflowNodeDefinitions.TriggerUses, "other", Trigger: new(WorkflowTriggerType.Manual, false, [], null)),
            new("other", "builtins.plan", "join")] };
        Assert.Contains(new WorkflowDefinitionValidator().Validate(document).Errors, error => error.Message.Contains("unreachable from entry"));
    }

    [Fact]
    public async Task Branches_start_together_and_join_waits_for_every_parent_across_restart()
    {
        var (store, workflow) = await Setup(Diamond()); var agent = new DeferredAgent();
        WorkflowOrchestrator Restart() => new(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new SnapshotPrompt());
        await Restart().AdvanceAsync(workflow, default);
        agent.Complete(await Run(store, workflow, "start"), "entry plan");
        await Restart().AdvanceAsync(workflow, default);
        await Restart().AdvanceAsync(workflow, default);
        Assert.Equal(3, agent.Tasks.Count);
        Assert.Equal(TaskRunStatus.Running, (await Run(store, workflow, "a")).Status);
        Assert.Equal(TaskRunStatus.Running, (await Run(store, workflow, "b")).Status);
        agent.Complete(await Run(store, workflow, "a"), "A plan");
        await Restart().AdvanceAsync(workflow, default);
        await Restart().AdvanceAsync(workflow, default);
        Assert.DoesNotContain(agent.Tasks, task => task.Prompt.StartsWith("join:"));
        Assert.NotEqual(WorkflowStatus.Completed, workflow.Status);
        agent.Complete(await Run(store, workflow, "b"), "B plan");
        await Restart().AdvanceAsync(workflow, default);
        await Restart().AdvanceAsync(workflow, default);
        Assert.Equal(4, agent.Tasks.Count);
        Assert.Contains("A plan", agent.Tasks[^1].Prompt);
        Assert.Contains("B plan", agent.Tasks[^1].Prompt);
        agent.Complete(await Run(store, workflow, "join"), "joined plan");
        await Restart().AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal("joined plan", workflow.PlanArtifact);
        await Restart().AdvanceAsync(workflow, default);
        Assert.Equal(4, agent.Tasks.Count);
    }

    [Fact]
    public async Task Script_branches_and_join_use_dependency_scheduling()
    {
        var (store, workflow) = await Setup(Diamond(scripts: true)); var agent = new DeferredAgent();
        var orchestrator = new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new SnapshotPrompt());
        await orchestrator.AdvanceAsync(workflow, default);
        agent.Complete(await Run(store, workflow, "start"), "plan");
        await orchestrator.AdvanceAsync(workflow, default);
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(2, agent.Tasks.Count(task => task.Kind == TaskRunKind.Script));
        agent.Complete(await Run(store, workflow, "a"), "a");
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Running, workflow.Status);
        Assert.Null(await store.GetTaskRunExecutionAsync(workflow.Id, "join", null, default));
        agent.Complete(await Run(store, workflow, "b"), "b");
        await orchestrator.AdvanceAsync(workflow, default);
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal("echo joined", agent.Tasks[^1].Script!.Script);
        agent.Complete(await Run(store, workflow, "join"), "joined");
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
    }

    [Fact]
    public async Task Failure_blocks_join_and_retry_preserves_successful_siblings()
    {
        var (store, workflow) = await Setup(Diamond()); var agent = new DeferredAgent();
        WorkflowOrchestrator Restart() => new(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new SnapshotPrompt());
        await Restart().AdvanceAsync(workflow, default);
        agent.Complete(await Run(store, workflow, "start"), "plan");
        await Restart().AdvanceAsync(workflow, default); await Restart().AdvanceAsync(workflow, default);
        agent.Complete(await Run(store, workflow, "a"), "A plan");
        agent.Complete(await Run(store, workflow, "b"), "failed", success: false);
        await Restart().AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status);
        Assert.Null(await store.GetTaskRunExecutionAsync(workflow.Id, "join", null, default));
        await new WorkflowService(store).RetryWorkflowAsync(workflow.Id, default);
        await Restart().AdvanceAsync(workflow, default);
        Assert.Equal(4, agent.Tasks.Count);
        Assert.Equal(TaskRunStatus.Succeeded, (await Run(store, workflow, "a")).Status);
        agent.Complete(await Run(store, workflow, "b"), "B retry");
        await Restart().AdvanceAsync(workflow, default); await Restart().AdvanceAsync(workflow, default);
        Assert.Contains("A plan", agent.Tasks[^1].Prompt);
        Assert.Contains("B retry", agent.Tasks[^1].Prompt);
    }

    [Fact]
    public async Task Nested_forks_and_multiple_terminals_do_not_complete_early()
    {
        var document = Diamond();
        document = document with { Steps = document.Steps.Select(step => step.Id == "a" ? step with { NextStepId = "c", NextStepIds = ["d"] } : step)
            .Concat([new WorkflowDefinitionStep("c", "builtins.plan", "join"), new("d", "builtins.plan")]).ToArray() };
        var (store, workflow) = await Setup(document); var agent = new DeferredAgent();
        var orchestrator = new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new SnapshotPrompt());
        for (var tick = 0; tick < 8; tick++)
        {
            await orchestrator.AdvanceAsync(workflow, default);
            foreach (var run in await store.ListTaskRunsAsync(workflow.Id, default))
                if (run.Status == TaskRunStatus.Running && run.DefinitionStepId != "d") agent.Complete(run, run.DefinitionStepId);
        }
        Assert.Equal(TaskRunStatus.Succeeded, (await Run(store, workflow, "join")).Status);
        Assert.NotEqual(WorkflowStatus.Completed, workflow.Status);
        agent.Complete(await Run(store, workflow, "d"), "d");
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal(6, agent.Tasks.Count);
    }

    [Fact]
    public async Task Pausing_polls_running_branches_without_starting_the_join()
    {
        var (store, workflow) = await Setup(Diamond()); var agent = new DeferredAgent();
        var orchestrator = new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new SnapshotPrompt());
        await orchestrator.AdvanceAsync(workflow, default);
        agent.Complete(await Run(store, workflow, "start"), "plan");
        await orchestrator.AdvanceAsync(workflow, default); await orchestrator.AdvanceAsync(workflow, default);
        workflow.IsPaused = true;
        agent.Complete(await Run(store, workflow, "a"), "A"); agent.Complete(await Run(store, workflow, "b"), "B");
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(3, agent.Tasks.Count);
        Assert.Null(await FindRun(store, workflow, "join"));
        workflow.IsPaused = false;
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(4, agent.Tasks.Count);
    }

    [Fact]
    public async Task Custom_join_can_bind_an_output_from_either_required_branch()
    {
        var catalog = new CustomTaskService(new InMemoryCustomTaskStore());
        var custom = await catalog.CreateAsync(new("Join", "Use {{input.summary}}", Inputs: [new("summary", "string", true)]), default);
        var document = Diamond(scripts: true);
        document = document with { Steps = document.Steps.Select(step => step.Id == "join"
            ? step with { Uses = CustomTaskDefinitions.Uses, Script = null, CustomTask = new(custom.Id,
                Bindings: new Dictionary<string, CustomTaskInputBinding> { ["summary"] = new("a", "output") }) } : step).ToArray() };
        var (store, workflow) = await Setup(document, catalog); var agent = new DeferredAgent();
        var orchestrator = new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new SnapshotPrompt());
        await orchestrator.AdvanceAsync(workflow, default);
        agent.Complete(await Run(store, workflow, "start"), "plan");
        await orchestrator.AdvanceAsync(workflow, default); await orchestrator.AdvanceAsync(workflow, default);
        agent.Complete(await Run(store, workflow, "a"), "bound summary");
        agent.Complete(await Run(store, workflow, "b"), "other branch");
        await orchestrator.AdvanceAsync(workflow, default); await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(TaskRunKind.Custom, agent.Tasks[^1].Kind);
        Assert.Contains("bound summary", agent.Tasks[^1].Prompt);
        var prepared = (await Run(store, workflow, "join")).CustomTaskExecutionJson!;
        Assert.Contains("bound summary", prepared);
        agent.Complete(await Run(store, workflow, "join"), "joined");
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
    }

    [Fact]
    public void Graph_normalization_preserves_trigger_entries_and_validates_trigger_settings()
    {
        var document = Diamond();
        document = document with { Steps = [..document.Steps, new("trigger", WorkflowNodeDefinitions.TriggerUses, "start",
            Trigger: new(WorkflowTriggerType.DevOpsIssueLabel, true, [Guid.NewGuid()], "ready"))] };
        Assert.True(new WorkflowDefinitionValidator().Validate(document).IsValid);
        Assert.Equal("start", Assert.Single(WorkflowNodeDefinitions.Normalize(document).Triggers!).NextStepId);
        document = document with { Steps = document.Steps.Select(step => step.Trigger is null ? step
            : step with { Trigger = step.Trigger with { Label = null } }).ToArray() };
        Assert.False(new WorkflowDefinitionValidator().Validate(document).IsValid);
    }

    private static Task<TaskRun?> FindRun(InMemoryWorkflowStore store, Workflow workflow, string id)
        => store.GetTaskRunExecutionAsync(workflow.Id, id, null, default);
    private static async Task<TaskRun> Run(InMemoryWorkflowStore store, Workflow workflow, string id) => (await FindRun(store, workflow, id))!;

    private static async Task<(InMemoryWorkflowStore, Workflow)> Setup(WorkflowDefinitionDocument document, CustomTaskService? catalog = null)
    {
        var store = new InMemoryWorkflowStore(); var definitions = new WorkflowDefinitionService(store, new(), customTasks: catalog);
        var definition = await definitions.CreateAsync(new("Graph"), default);
        var version = await definitions.CreateVersionAsync(definition.Id, new(null, true, false, document), default);
        var result = await new WorkflowService(store, workflowDefinitions: definitions).StartGitHubIssueWorkflowAsync(new(
            "https://example.test/issues/1", "https://example.test/repo", null, null,
            WorkflowDefinitionId: definition.Id, WorkflowDefinitionVersionId: version.Id), default);
        return (store, (await store.GetWorkflowAsync(result.WorkflowId, default))!);
    }

    private sealed class SnapshotPrompt : IPromptRenderer
    {
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? item, CancellationToken token)
            => Task.FromResult($"{workflow.CurrentDefinitionStepId}:{workflow.PlanArtifact}");
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? item, IReadOnlyList<PullRequestComment> comments, CancellationToken token)
            => RenderAsync(kind, workflow, item, token);
    }

    private sealed class DeferredAgent : IAgentRunner
    {
        public List<AgentTask> Tasks { get; } = [];
        private readonly Dictionary<string, AgentRunResult> results = [];
        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken token)
        {
            Tasks.Add(task);
            return Task.FromResult(new AgentRunStartResult(task.ExecutionAttemptId!.Value.ToString("N")));
        }
        public Task<AgentRunResult?> TryGetResultAsync(string id, CancellationToken token) => Task.FromResult(results.GetValueOrDefault(id));
        public void Complete(TaskRun run, string output, bool success = true)
            => results[run.ExternalId!] = new(success, run.ExternalId!, output, success ? null : "Task failed");
    }
}
