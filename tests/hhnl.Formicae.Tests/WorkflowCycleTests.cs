using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowCycleTests
{
    private static WorkflowDefinitionStep Script(string id, string? next = null, string[]? others = null)
        => new(id, "builtins.script", next, Script: new("echo value"), NextStepIds: others);

    [Theory]
    [InlineData("formicae.workflow/v1alpha1", false)]
    [InlineData("formicae.workflow/v1alpha2", false)]
    [InlineData("formicae.workflow/v1alpha3", false)]
    [InlineData("formicae.workflow/v1alpha1", true)]
    [InlineData("formicae.workflow/v1alpha2", true)]
    [InlineData("formicae.workflow/v1alpha3", true)]
    public void Control_cycles_need_no_count_or_exit(string schema, bool self)
    {
        var doc = new WorkflowDefinitionDocument(schema, "a", self ? [Script("a", "a")] : [Script("a", "b"), Script("b", "a")]);
        Assert.True(new WorkflowDefinitionValidator().Validate(doc).IsValid);
    }

    [Fact]
    public async Task Repeated_visits_survive_restart_pause_and_cancel_without_reusing_completed_runs()
    {
        var (store, workflow, agent) = await Setup(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "a", [Script("a", "a")]));
        for (var visit = 1; visit <= 6; visit++)
        {
            await Restart(store, agent).AdvanceAsync(workflow, default);
            var run = (await store.GetTaskRunExecutionAsync(workflow.Id, "a", visit, default))!;
            Assert.Equal(TaskRunStatus.Running, run.Status);
            agent.Complete(run, "value " + visit);
            await Restart(store, agent).AdvanceAsync(workflow, default);
            Assert.Equal(TaskRunStatus.Succeeded, run.Status);
            Assert.Equal(visit, WorkflowCycleDefinitions.State((await store.GetWorkflowAsync(workflow.Id, default))!).Completed["a"]);
        }
        var runs = await store.ListTaskRunsAsync(workflow.Id, default);
        Assert.Equal(6, runs.Count);
        Assert.Equal(6, runs.Select(run => run.ExecutionAttemptId).Distinct().Count());
        var controls = new WorkflowExecutionService(store, new SystemClock());
        await controls.SetControlAsync(workflow.Id, "pause", default);
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(6, agent.Tasks.Count);
        await controls.SetControlAsync(workflow.Id, "resume", default);
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(7, agent.Tasks.Count);
        await controls.SetControlAsync(workflow.Id, "cancel", default);
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Canceled, workflow.Status);
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(7, agent.Tasks.Count);
    }

    [Fact]
    public async Task Cyclic_fork_join_waits_for_each_branch_on_every_pass()
    {
        var (store, workflow, agent) = await Setup(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "a", [
            Script("a", "b", ["c"]), Script("b", "join"), Script("c", "join"), Script("join", "a")]));
        for (var pass = 1; pass <= 3; pass++)
        {
            await Restart(store, agent).AdvanceAsync(workflow, default);
            await Finish("a", pass);
            await Restart(store, agent).AdvanceAsync(workflow, default);
            Assert.NotNull(await store.GetTaskRunExecutionAsync(workflow.Id, "b", pass, default));
            Assert.NotNull(await store.GetTaskRunExecutionAsync(workflow.Id, "c", pass, default));
            await Finish("b", pass);
            await Restart(store, agent).AdvanceAsync(workflow, default);
            Assert.Null(await store.GetTaskRunExecutionAsync(workflow.Id, "join", pass, default));
            await Finish("c", pass);
            await Restart(store, agent).AdvanceAsync(workflow, default);
            await Finish("join", pass);
        }
        Assert.Equal(12, agent.Tasks.Count);
        async Task Finish(string id, int pass)
        {
            agent.Complete((await store.GetTaskRunExecutionAsync(workflow.Id, id, pass, default))!, id);
            await Restart(store, agent).AdvanceAsync(workflow, default);
        }
    }

    [Fact]
    public async Task Finite_prefix_is_not_repeated_but_cycle_exit_receives_each_pass()
    {
        var (store, workflow, agent) = await Setup(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "prefix", [
            Script("prefix", "a"), Script("a", "b"), Script("b", "a", ["exit"]), Script("exit")]));
        for (var tick = 0; tick < 16; tick++)
        {
            await Restart(store, agent).AdvanceAsync(workflow, default);
            foreach (var run in (await store.ListTaskRunsAsync(workflow.Id, default)).Where(run => run.Status == TaskRunStatus.Running)) agent.Complete(run, "value");
        }
        var runs = await store.ListTaskRunsAsync(workflow.Id, default);
        Assert.Single(runs, run => run.DefinitionStepId == "prefix");
        Assert.True(runs.Count(run => run.DefinitionStepId == "a") >= 3);
        Assert.True(runs.Count(run => run.DefinitionStepId == "exit") >= 2);
        Assert.NotEqual(WorkflowStatus.Completed, workflow.Status);
    }

    [Fact]
    public async Task Decisions_are_evaluated_and_persisted_per_visit()
    {
        var decision = new WorkflowDefinitionStep("choose", WorkflowDecisionDefinitions.Uses, Decision: new(
            new("literal", "boolean", "equals", Value: JsonSerializer.SerializeToElement(true), CompareTo: JsonSerializer.SerializeToElement(true)), "a", "exit"));
        var (store, workflow, agent) = await Setup(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "a", [Script("a", "choose"), decision, Script("exit")]));
        for (var tick = 0; tick < 12; tick++)
        {
            await Restart(store, agent).AdvanceAsync(workflow, default);
            foreach (var run in (await store.ListTaskRunsAsync(workflow.Id, default)).Where(run => run.Status == TaskRunStatus.Running)) agent.Complete(run, "value");
        }
        var routes = await store.ListDecisionExecutionsAsync(workflow.Id, default);
        Assert.True(routes.Count >= 3);
        Assert.Equal(routes.Count, routes.Select(route => route.VisitIteration).Distinct().Count());
        Assert.All(routes, route => Assert.Equal("a", route.SelectedTargetId));
        Assert.DoesNotContain(await store.ListTaskRunsAsync(workflow.Id, default), run => run.DefinitionStepId == "exit");
    }

    [Fact]
    public async Task Feedback_variable_uses_default_then_previous_successful_visit_and_freezes_retry()
    {
        var consumer = new WorkflowDefinitionStep("consumer", CustomTaskDefinitions.AgentUses, "producer", CustomTask: new("agent:consumer",
            Bindings: new Dictionary<string, CustomTaskInputBinding> { ["text"] = new("feedback", "value") },
            Definition: new("Use {{input.text}}", [new("text", "string", true, JsonSerializer.SerializeToElement("initial"))], new())));
        var (store, workflow, agent) = await Setup(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "consumer", [consumer, Script("producer", "consumer")],
            Variables: [new("feedback", "Feedback", "string", Sources: [new("producer", "output")])]));
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal("Use initial", agent.Tasks[^1].Prompt);
        var first = (await store.GetTaskRunExecutionAsync(workflow.Id, "consumer", 1, default))!;
        var initial = JsonSerializer.Deserialize<PreparedCustomTaskExecution>(first.CustomTaskExecutionJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(initial.Provenance!["text"].Variable!.Sources[0].Unavailable);
        agent.Complete(first, "done"); await Restart(store, agent).AdvanceAsync(workflow, default);
        await Restart(store, agent).AdvanceAsync(workflow, default);
        var producer = (await store.GetTaskRunExecutionAsync(workflow.Id, "producer", 1, default))!;
        agent.Complete(producer, "previous"); await Restart(store, agent).AdvanceAsync(workflow, default);
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal("Use previous", agent.Tasks[^1].Prompt);
        var second = (await store.GetTaskRunExecutionAsync(workflow.Id, "consumer", 2, default))!;
        agent.Complete(second, "", false); await Restart(store, agent).AdvanceAsync(workflow, default);
        await new WorkflowService(store).RetryWorkflowAsync(workflow.Id, default);
        producer.StructuredOutputsJson = "{\"output\":\"mutated\"}";
        await store.UpsertTaskRunAsync(producer, default);
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal("Use previous", agent.Tasks[^1].Prompt);
        Assert.Equal(2, second.LoopIteration);
    }

    [Fact]
    public async Task Bounded_loop_restarts_its_count_when_an_outer_cycle_reenters()
    {
        var doc = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "loop", [
            new("loop", WorkflowNodeDefinitions.LoopUses, "after", Loop: new("body", 2, 2)),
            Script("body", "loop") with { NextStepPort = "return" }, Script("after", "loop")]);
        var (store, workflow, agent) = await Setup(doc);
        for (var tick = 0; tick < 20; tick++)
        {
            await Restart(store, agent).AdvanceAsync(workflow, default);
            foreach (var run in (await store.ListTaskRunsAsync(workflow.Id, default)).Where(run => run.Status == TaskRunStatus.Running)) agent.Complete(run, "value");
        }
        var runs = await store.ListTaskRunsAsync(workflow.Id, default);
        Assert.True(runs.Count(run => run.DefinitionStepId == "after") >= 3);
        var completedBodies = runs.Count(run => run.DefinitionStepId == "body" && run.Status == TaskRunStatus.Succeeded);
        var completedExits = runs.Count(run => run.DefinitionStepId == "after" && run.Status == TaskRunStatus.Succeeded);
        Assert.InRange(completedBodies - 2 * completedExits, 0, 2);
        Assert.True((await store.ListLoopIterationsAsync(workflow.Id, default)).Count >= 6);
        Assert.NotEqual(WorkflowStatus.Failed, workflow.Status);
    }

    [Fact]
    public async Task Parallel_group_can_be_reentered_with_distinct_branch_runs_and_history()
    {
        var doc = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "group", [
            new("group", WorkflowParallelDefinitions.Uses, "after", Parallel: new(["left", "right"])),
            new("left", "builtins.plan", "group", NextStepPort: "join"),
            new("right", "builtins.plan", "group", NextStepPort: "join"), Script("after", "group")]);
        var (store, workflow, agent) = await Setup(doc);
        var failedOnce = false;
        for (var tick = 0; tick < 24; tick++)
        {
            await Restart(store, agent).AdvanceAsync(workflow, default);
            if (workflow.Status == WorkflowStatus.Failed)
            {
                await new WorkflowService(store).RetryWorkflowAsync(workflow.Id, default);
                Assert.Equal("group", workflow.CurrentDefinitionStepId);
            }
            foreach (var run in (await store.ListTaskRunsAsync(workflow.Id, default)).Where(run => run.Status == TaskRunStatus.Running))
            {
                var fail = !failedOnce && run.DefinitionStepId == "left" && run.LoopIteration == 2;
                failedOnce |= fail; agent.Complete(run, "plan contents", !fail);
            }
        }
        Assert.True(failedOnce);
        var attempts = await store.ListTaskRunAttemptsAsync(workflow.Id, default);
        var leftId = (await store.GetTaskRunExecutionAsync(workflow.Id, "left", 2, default))!.Id;
        var rightId = (await store.GetTaskRunExecutionAsync(workflow.Id, "right", 2, default))!.Id;
        Assert.Single(attempts, attempt => attempt.TaskRunId == leftId);
        Assert.DoesNotContain(attempts, attempt => attempt.TaskRunId == rightId);
        var groups = await store.ListParallelExecutionsAsync(workflow.Id, default);
        Assert.True(groups.Count >= 3);
        Assert.Equal(groups.Count, groups.Select(group => group.VisitIteration).Distinct().Count());
        var execution = (await new WorkflowExecutionService(store, new SystemClock()).GetExecutionAsync(workflow.Id, default))!;
        Assert.Equal(groups.Count, execution.Parallels.Count);
        Assert.All(execution.Parallels, group => Assert.NotNull(group.VisitIteration));
        Assert.NotEqual(WorkflowStatus.Failed, workflow.Status);
    }

    [Fact]
    public async Task Cyclic_planning_join_uses_both_branch_plans_from_its_activation()
    {
        var (store, workflow, agent) = await Setup(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "entry", [
            Script("entry", "a", ["b"]), new("a", "builtins.plan", "join"), new("b", "builtins.plan", "join"),
            new("join", "builtins.plan", "entry")]));
        for (var pass = 1; pass <= 2; pass++)
        {
            await Restart(store, agent).AdvanceAsync(workflow, default);
            await Finish("entry", pass, "entry");
            await Restart(store, agent).AdvanceAsync(workflow, default);
            await Finish("a", pass, "A plan " + pass);
            await Finish("b", pass, "B plan " + pass);
            await Restart(store, agent).AdvanceAsync(workflow, default);
            var prompt = agent.Tasks[^1].Prompt;
            Assert.Contains("A plan " + pass, prompt); Assert.Contains("B plan " + pass, prompt);
            await Finish("join", pass, "Joined plan " + pass);
        }
        async Task Finish(string id, int visit, string output)
        {
            agent.Complete((await store.GetTaskRunExecutionAsync(workflow.Id, id, visit, default))!, output);
            await Restart(store, agent).AdvanceAsync(workflow, default);
        }
    }

    [Fact]
    public async Task Completed_visit_is_not_relaunched_when_cursor_write_is_lost()
    {
        var (store, workflow, agent) = await Setup(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "a", [Script("a", "a")]));
        await Restart(store, agent).AdvanceAsync(workflow, default);
        var beforeCompletion = workflow.CycleExecutionJson;
        var run = (await store.GetTaskRunExecutionAsync(workflow.Id, "a", 1, default))!;
        agent.Complete(run, "first visit"); await Restart(store, agent).AdvanceAsync(workflow, default);
        workflow.CycleExecutionJson = beforeCompletion; await store.UpdateWorkflowAsync(workflow, default);
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Single(agent.Tasks);
        Assert.Equal(1, WorkflowCycleDefinitions.State(workflow).Completed["a"]);
        await Restart(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(2, agent.Tasks.Count);
    }

    [Fact]
    public async Task Selected_acyclic_entry_completes_when_another_entry_contains_a_cycle()
    {
        var (store, workflow, agent) = await Setup(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "entry", [
            Script("entry", "left", ["right"]), Script("left"), Script("right"),
            new("other", WorkflowNodeDefinitions.TriggerUses, "repeat", Trigger: new(WorkflowTriggerType.Manual, false, [], null)),
            Script("repeat", "repeat")]));
        for (var tick = 0; tick < 8; tick++)
        {
            await Restart(store, agent).AdvanceAsync(workflow, default);
            foreach (var run in (await store.ListTaskRunsAsync(workflow.Id, default)).Where(run => run.Status == TaskRunStatus.Running)) agent.Complete(run, "done");
        }
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal(3, agent.Tasks.Count);
        Assert.DoesNotContain(await store.ListTaskRunsAsync(workflow.Id, default), run => run.DefinitionStepId == "repeat");
    }

    private static WorkflowOrchestrator Restart(InMemoryWorkflowStore store, Agent agent)
        => new(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new Prompt());
    private static async Task<(InMemoryWorkflowStore Store, Workflow Workflow, Agent Agent)> Setup(WorkflowDefinitionDocument document)
    {
        var store = new InMemoryWorkflowStore(); var definitions = new WorkflowDefinitionService(store, new());
        var definition = await definitions.CreateAsync(new("Cycle"), default);
        var version = await definitions.CreateVersionAsync(definition.Id, new(null, true, false, document), default);
        var start = await new WorkflowService(store, workflowDefinitions: definitions).StartGitHubIssueWorkflowAsync(new(
            "https://example.test/issues/1", "https://example.test/repo", null, null,
            WorkflowDefinitionId: definition.Id, WorkflowDefinitionVersionId: version.Id), default);
        return (store, (await store.GetWorkflowAsync(start.WorkflowId, default))!, new());
    }
    private sealed class Prompt : IPromptRenderer
    {
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? item, CancellationToken token) => Task.FromResult(workflow.PlanArtifact ?? "test");
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? item, IReadOnlyList<PullRequestComment> comments, CancellationToken token) => Task.FromResult("test");
    }
    private sealed class Agent : IAgentRunner
    {
        public List<AgentTask> Tasks { get; } = [];
        private readonly Dictionary<string, AgentRunResult> results = [];
        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken token)
        { Tasks.Add(task); return Task.FromResult(new AgentRunStartResult(task.ExecutionAttemptId!.Value.ToString("N"))); }
        public Task<AgentRunResult?> TryGetResultAsync(string id, CancellationToken token) => Task.FromResult(results.GetValueOrDefault(id));
        public Task CancelAsync(string id, CancellationToken token) { results.Remove(id); return Task.CompletedTask; }
        public void Complete(TaskRun run, string output, bool success = true) => results[run.ExternalId!] = new(success, run.ExternalId!, output, success ? null : "Failed");
    }
}
