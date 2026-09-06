using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class CustomTaskOrchestratorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("true")]
    public async Task Custom_runs_without_issue_labels_or_builtin_side_effects_and_preserves_output(string output)
    {
        var (store, workflow) = await SetupAsync(); var agent = new Agent { Immediate = output };
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal("original plan", workflow.PlanArtifact); Assert.Null(workflow.PullRequestUrl);
        var task = Assert.Single(agent.Tasks); Assert.Equal(TaskRunKind.Custom, task.Kind);
        Assert.Equal(43, task.TimeoutSeconds); Assert.NotNull(task.ExecutionAttemptId);
        Assert.Equal("node-model", task.Model); Assert.Equal("node-ai", task.AiSettingsId);
        Assert.Equal("Inspect original plan", task.Prompt);
        var context = Assert.Single(task.ContextFiles!); Assert.Equal("custom-task-inputs.json", context.FileName);
        using var json = JsonDocument.Parse(context.Content);
        Assert.Equal("original plan", json.RootElement.GetProperty("workflowFields").GetProperty("planArtifact").GetString());
        var run = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default));
        Assert.Equal(output, run.Output); Assert.Equal(TaskRunStatus.Succeeded, run.Status); Assert.NotNull(run.CustomTaskExecutionJson);
    }

    [Fact]
    public async Task Lost_launch_response_reuses_prepared_context_and_attempt_after_restart()
    {
        var (store, workflow) = await SetupAsync(); var agent = new Agent { Uncertain = true };
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        var before = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default));
        Assert.Equal(TaskRunStatus.Running, before.Status); Assert.Null(before.ExternalId);
        workflow.PlanArtifact = "changed after launch";
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(2, agent.Tasks.Count);
        Assert.Equal(agent.Tasks[0].ExecutionAttemptId, agent.Tasks[1].ExecutionAttemptId);
        Assert.Equal(agent.Tasks[0].Prompt, agent.Tasks[1].Prompt);
        Assert.Equal(agent.Tasks[0].ContextFiles![0].Content, agent.Tasks[1].ContextFiles![0].Content);
        Assert.Equal(before.CustomTaskExecutionJson, (await store.ListTaskRunsAsync(workflow.Id, default))[0].CustomTaskExecutionJson);
        Assert.Equal(WorkflowStatus.Running, workflow.Status);
    }

    [Fact]
    public async Task Explicit_retry_keeps_prepared_values_but_gets_new_attempt()
    {
        var (store, workflow) = await SetupAsync(); var agent = new Agent { Permanent = true };
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status);
        var initial = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default)); var payload = initial.CustomTaskExecutionJson;
        await new WorkflowService(store).RetryWorkflowAsync(workflow.Id, default);
        workflow.PlanArtifact = "changed"; agent.Permanent = false; agent.Immediate = "done";
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal(agent.Tasks[0].Prompt, agent.Tasks[1].Prompt);
        Assert.NotEqual(agent.Tasks[0].ExecutionAttemptId, agent.Tasks[1].ExecutionAttemptId);
        Assert.Equal(payload, (await store.ListTaskRunsAsync(workflow.Id, default))[0].CustomTaskExecutionJson);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{bad")]
    [InlineData("{}")]
    public async Task Corrupt_saved_preparation_fails_without_launch_or_recapture(string payload)
    {
        var (store, workflow) = await SetupAsync(); var agent = new Agent();
        await store.UpsertTaskRunAsync(new() { WorkflowId = workflow.Id, DefinitionStepId = "custom", Kind = TaskRunKind.Custom,
            CustomTaskExecutionJson = payload }, default);
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status); Assert.Empty(agent.Tasks);
        Assert.Equal(payload, (await store.ListTaskRunsAsync(workflow.Id, default))[0].CustomTaskExecutionJson);
    }

    [Fact]
    public async Task Whitespace_rendered_prompt_fails_before_launch()
    {
        var snapshot = Snapshot() with { PromptTemplate = "{{input.optional}}", Inputs = [new("optional", "string")] };
        var (store, workflow) = await SetupAsync(snapshot: snapshot); var agent = new Agent();
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status); Assert.Empty(agent.Tasks);
    }

    [Fact]
    public async Task Poll_failures_keep_job_running_then_authoritative_output_completes()
    {
        var (store, workflow) = await SetupAsync(); var agent = new Agent();
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        agent.PollFailure = true; await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Running, workflow.Status); Assert.Single(agent.Tasks);
        agent.PollFailure = false; agent.PollOutput = "authoritative";
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal("authoritative", (await store.ListTaskRunsAsync(workflow.Id, default))[0].Output);
    }

    [Fact]
    public async Task Oversized_result_is_bounded_failed_and_not_a_successful_decision_input()
    {
        var (store, workflow) = await SetupAsync(); var agent = new Agent { Immediate = new string('x', 262145) };
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status);
        var run = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default));
        Assert.Equal(262144, run.Output!.Length); Assert.Equal(TaskRunStatus.Failed, run.Status); Assert.Contains("truncated", run.Output);
    }

    [Fact]
    public async Task Terminal_output_survives_logging_failure_and_next_tick_finishes_without_relaunch()
    {
        var (store, workflow) = await SetupAsync(); var agent = new Agent { Immediate = "done" };
        var fault = System.Reflection.DispatchProxy.Create<IWorkflowStore, WorkflowParallelOrchestratorTests.StoreFaultProxy>();
        var proxy = (WorkflowParallelOrchestratorTests.StoreFaultProxy)fault; proxy.Inner = store; proxy.FailAssignment = false;
        await Orchestrator(fault, agent).AdvanceAsync(workflow, default);
        Assert.Equal(TaskRunStatus.Succeeded, (await store.ListTaskRunsAsync(workflow.Id, default))[0].Status);
        proxy.FailLogs = false;
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status); Assert.Single(agent.Tasks);
    }

    [Fact]
    public async Task Loop_iterations_have_distinct_preparations_and_attempts()
    {
        var steps = new WorkflowDefinitionStep[] {
            new("loop", "builtins.loop", "finish", Loop: new("custom", 2, 2)),
            Custom("custom", "loop") with { NextStepPort = "return" }, Custom("finish") };
        var (store, workflow) = await SetupAsync(steps: steps, start: "loop"); var agent = new Agent { Immediate = "done" };
        for (var i = 0; i < 6; i++) await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        var runs = await store.ListTaskRunsAsync(workflow.Id, default);
        Assert.Equal(3, runs.Count); Assert.Equal(3, runs.Select(run => run.ExecutionAttemptId).Distinct().Count());
        Assert.Equal(new int?[] { 1, 2 }, runs.Where(run => run.DefinitionStepId == "custom").Select(run => run.LoopIteration).Order());
    }

    [Fact]
    public async Task Custom_missing_cursor_fails_instead_of_restarting_at_definition_entry()
    {
        var (store, workflow) = await SetupAsync(); var agent = new Agent();
        workflow.CurrentStep = WorkflowStep.Custom; workflow.CurrentDefinitionStepId = null;
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status); Assert.Empty(agent.Tasks);
        Assert.Contains("exact definition step cursor", workflow.FailureReason);
    }

    [Fact]
    public async Task Custom_does_not_reuse_unidentified_legacy_kind_run()
    {
        var (store, workflow) = await SetupAsync(); var agent = new Agent { Immediate = "new exact run" };
        await store.UpsertTaskRunAsync(new() { WorkflowId = workflow.Id, Kind = TaskRunKind.Custom, DefinitionStepId = "",
            Status = TaskRunStatus.Succeeded, Output = "unidentified" }, default);
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status); Assert.Single(agent.Tasks);
        var runs = await store.ListTaskRunsAsync(workflow.Id, default);
        Assert.Equal(2, runs.Count); Assert.Equal("new exact run", runs.Single(run => run.DefinitionStepId == "custom").Output);
    }

    [Fact]
    public async Task Immediate_terminal_result_survives_event_failure_without_repoll_or_relaunch()
    {
        var (store, workflow) = await SetupAsync(); var agent = new Agent { Immediate = "terminal" };
        var fault = System.Reflection.DispatchProxy.Create<IWorkflowStore, TerminalEventFault>();
        ((TerminalEventFault)fault).Inner = store;
        await Orchestrator(fault, agent).AdvanceAsync(workflow, default);
        var run = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default));
        Assert.Equal(TaskRunStatus.Succeeded, run.Status); Assert.Equal("terminal", run.Output);
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status); Assert.Single(agent.Tasks);
    }

    public class TerminalEventFault : System.Reflection.DispatchProxy
    {
        public IWorkflowStore Inner = null!;
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowStore.AddEventAsync) && args![0] is WorkflowEvent { Type: WorkflowEventTypes.TaskSucceeded })
                throw new InvalidOperationException("Terminal event store unavailable");
            try { return method.Invoke(Inner, args); }
            catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
        }
    }

    private static CustomTaskSnapshot Snapshot() => new("task", 1, "Inspect", "", "Inspect {{workflow.planArtifact}}", [], new(TimeoutSeconds: 43));
    private static WorkflowDefinitionStep Custom(string id, string? next = null) => new(id, CustomTaskDefinitions.Uses, next,
        Model: "node-model", AiSettingsId: "node-ai", CustomTask: new("task", Snapshot: Snapshot()));
    private static async Task<(InMemoryWorkflowStore, Workflow)> SetupAsync(CustomTaskSnapshot? snapshot = null,
        IReadOnlyList<WorkflowDefinitionStep>? steps = null, string start = "custom")
    {
        var store = new InMemoryWorkflowStore();
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, start,
            steps ?? [Custom("custom") with { CustomTask = new("task", Snapshot: snapshot ?? Snapshot()) }]);
        var validation = new WorkflowDefinitionValidator().Validate(document);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Select(error => error.Message)));
        var definition = await store.CreateWorkflowDefinitionAsync(new() { Name = "custom" }, default);
        var version = await store.CreateWorkflowDefinitionVersionAsync(new() { WorkflowDefinitionId = definition.Id, Version = 1,
            DslSchemaVersion = document.Schema, DefinitionJson = WorkflowDefinitionJson.Serialize(document), IsEnabled = true }, default);
        var workflow = await store.CreateWorkflowAsync(new() { IssueUrl = "https://example.test/issues/1", RepositoryUrl = "https://example.test/repo",
            WorkflowDefinitionId = definition.Id, WorkflowDefinitionVersionId = version.Id, CurrentDefinitionStepId = WorkflowNodeDefinitions.Normalize(document).StartStepId,
            PlanArtifact = "original plan" }, default);
        return (store, workflow);
    }
    private static WorkflowOrchestrator Orchestrator(IWorkflowStore store, Agent agent) => new(store,
        System.Reflection.DispatchProxy.Create<IWorkItemProvider, ForbiddenProvider>(),
        System.Reflection.DispatchProxy.Create<ISourceControlProvider, ForbiddenProvider>(), agent,
        System.Reflection.DispatchProxy.Create<IPromptRenderer, ForbiddenProvider>());
    public class ForbiddenProvider : System.Reflection.DispatchProxy
    { protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) => throw new InvalidOperationException("Unexpected built-in side effect: " + method!.Name); }
    private sealed class Agent : IAgentRunner
    {
        public List<AgentTask> Tasks = [];
        public string? Immediate; public bool Uncertain; public bool Permanent; public bool PollFailure; public string? PollOutput;
        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken token)
        {
            Tasks.Add(task); token.ThrowIfCancellationRequested();
            if (Uncertain) { Uncertain = false; throw new AgentLaunchUncertainException("lost response", new HttpRequestException()); }
            if (Permanent) throw new InvalidOperationException("bad configuration");
            var id = task.ExecutionAttemptId!.Value.ToString("N");
            return Task.FromResult(new AgentRunStartResult(id, Immediate is null ? null : new(true, id, Immediate, null)));
        }
        public Task<AgentRunResult?> TryGetResultAsync(string id, CancellationToken token)
        { if (PollFailure) throw new HttpRequestException("temporary"); return Task.FromResult<AgentRunResult?>(PollOutput is null ? null : new(true, id, PollOutput, null)); }
    }
}
