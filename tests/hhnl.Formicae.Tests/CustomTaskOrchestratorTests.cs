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

    [Fact]
    public async Task Producer_consumer_completion_survives_restart_and_consumer_retry_with_frozen_provenance()
    {
        var producer = Snapshot() with { Outputs = [new("summary", "string", true)] };
        var consumer = Snapshot() with { PromptTemplate = "Consume {{input.summary}}", Inputs = [new("summary", "string", true)] };
        var settings = new WorkflowCustomTaskSettings("task", Snapshot: consumer, Bindings: new Dictionary<string, CustomTaskInputBinding> { ["summary"] = new("producer", "summary") });
        var steps = new[] { Custom("producer", "consumer") with { CustomTask = new("task", Snapshot: producer) }, Custom("consumer") with { CustomTask = settings } };
        var (store, workflow) = await SetupAsync(steps: steps, start: "producer");
        var agent = new Agent { Immediate = "{\"summary\":\"ready\"}" };
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        var source = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default));
        Assert.Equal("ready", source.ToResponse().StructuredOutputs!["summary"].GetString());
        Assert.Contains("Output schema:", agent.Tasks[0].Prompt);
        agent.Immediate = null; agent.Permanent = true;
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status);
        var run = (await store.ListTaskRunsAsync(workflow.Id, default)).Single(run => run.DefinitionStepId == "consumer");
        var frozen = run.CustomTaskExecutionJson;
        var execution = run.ToResponse().CustomTaskExecution!;
        Assert.Equal(source.Id, execution.Provenance!["summary"].RunId);
        Assert.Equal(source.ExecutionAttemptId, execution.Provenance["summary"].ExecutionAttemptId);
        Assert.Equal("Consume ready", execution.Prompt);
        await new WorkflowService(store).RetryWorkflowAsync(workflow.Id, default);
        source.StructuredOutputsJson = "{\"summary\":\"changed\"}"; source.ExecutionAttemptId = Guid.NewGuid();
        agent.Permanent = false; agent.Immediate = "consumed";
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal(frozen, run.CustomTaskExecutionJson);
        Assert.Equal("Consume ready", agent.Tasks.Last().Prompt);
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("{}")]
    [InlineData("{\"summary\":42}")]
    public async Task Invalid_output_fails_producer_and_does_not_launch_consumer(string output)
    {
        var producer = Snapshot() with { Outputs = [new("summary", "string", true)] };
        var (store, workflow) = await SetupAsync(steps: [Custom("producer", "consumer") with { CustomTask = new("task", Snapshot: producer) }, Custom("consumer")], start: "producer");
        var agent = new Agent { Immediate = output };
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status); Assert.Single(agent.Tasks);
        var run = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default));
        Assert.Null(run.StructuredOutputsJson); Assert.Equal(TaskRunStatus.Failed, run.Status); Assert.NotNull(run.FailureReason);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Failed_execution_or_unrecognized_cli_logs_cannot_supply_structured_outputs(bool succeeded, bool finalResponse)
    {
        var (store, workflow) = await SetupAsync(snapshot: Snapshot() with { Outputs = [new("summary", "string", true)] });
        var agent = new Agent { Immediate = "{\"summary\":\"ready\"}", Succeeded = succeeded, FinalResponse = finalResponse };
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status);
        var run = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default)); Assert.Null(run.StructuredOutputsJson);
        await new WorkflowService(store).RetryWorkflowAsync(workflow.Id, default);
        agent.Succeeded = true; agent.FinalResponse = true;
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status); Assert.NotNull(run.StructuredOutputsJson);
    }

    [Fact]
    public async Task Unavailable_source_fails_before_external_launch()
    {
        var producer = Snapshot() with { Outputs = [new("summary", "string", true)] };
        var consumer = Snapshot() with { PromptTemplate = "Consume {{input.summary}}", Inputs = [new("summary", "string", true)] };
        var settings = new WorkflowCustomTaskSettings("task", Snapshot: consumer, Bindings: new Dictionary<string, CustomTaskInputBinding> { ["summary"] = new("producer", "summary") });
        var (store, workflow) = await SetupAsync(steps: [Custom("producer", "consumer") with { CustomTask = new("task", Snapshot: producer) }, Custom("consumer") with { CustomTask = settings }], start: "producer");
        workflow.CurrentDefinitionStepId = "consumer"; var agent = new Agent();
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status); Assert.Empty(agent.Tasks);
        Assert.Contains("requires successful validated outputs", workflow.FailureReason);
    }

    [Fact]
    public async Task Bound_inputs_resolve_outputs_from_the_same_loop_iteration()
    {
        var producer = Snapshot() with { Outputs = [new("summary", "string", true)] };
        var consumer = Snapshot() with { PromptTemplate = "Consume {{input.summary}}", Inputs = [new("summary", "string", true)] };
        var settings = new WorkflowCustomTaskSettings("task", Snapshot: consumer, Bindings: new Dictionary<string, CustomTaskInputBinding> { ["summary"] = new("producer", "summary") });
        var steps = new[] { new WorkflowDefinitionStep("loop", "builtins.loop", "finish", Loop: new("producer", 2, 2)),
            Custom("producer", "consumer") with { CustomTask = new("task", Snapshot: producer) },
            Custom("consumer", "loop") with { NextStepPort = "return", CustomTask = settings }, Custom("finish") };
        var (store, workflow) = await SetupAsync(steps: steps, start: "loop"); var agent = new Agent();
        for (var i = 0; i < 6; i++)
        {
            var iteration = (await store.ListTaskRunsAsync(workflow.Id, default)).Count(run => run.DefinitionStepId == "producer") + 1;
            agent.Immediate = workflow.CurrentDefinitionStepId == "producer" ? $"{{\"summary\":\"iteration{iteration}\"}}" : "consumed";
            await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        }
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        var consumers = (await store.ListTaskRunsAsync(workflow.Id, default)).Where(run => run.DefinitionStepId == "consumer").OrderBy(run => run.LoopIteration).ToArray();
        Assert.Equal(2, consumers.Length);
        for (var i = 0; i < 2; i++)
        {
            var execution = consumers[i].ToResponse().CustomTaskExecution!;
            Assert.Equal($"Consume iteration{i + 1}", execution.Prompt); Assert.Equal(i + 1, execution.Provenance!["summary"].LoopIteration);
        }
    }

    [Fact]
    public async Task Inline_agent_task_executes_with_persona_and_declared_outputs()
    {
        var definition = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "custom",
            [new("custom", CustomTaskDefinitions.AgentUses, PersonaId: "reviewer",
                PersonaSnapshot: new("reviewer", 1, "Reviewer", "Check the evidence", "Concise", ""),
                EnvironmentId: "agent-image", EnvironmentSnapshot: new("agent-image", 1, "Agent image", "", new() {
                    Image = new("registry.example.test/agent@sha256:" + new string('a', 64), "Always", ["registry-auth"]),
                    Runtime = new(TimeoutLimitSeconds: 50), Tools = [new("review-tool", "echo ready")],
                    McpServers = [new("review-mcp", Command: "review-server")] }),
                CustomTask: new("", Definition: new("Inspect {{workflow.planArtifact}}", [], new(TimeoutSeconds: 43), [new("summary", "string", true)])))]);
        var resolved = await CustomTaskDefinitions.ResolveAsync(definition, null, default);
        Assert.True(resolved.Validation.IsValid);
        var (store, workflow) = await SetupAsync(steps: resolved.Document.Steps);
        var agent = new Agent { Immediate = "{\"summary\":\"Checked\"}" };
        Assert.True(PersonaDefinitions.ValidateRuntime(resolved.Document).IsValid);
        Assert.True(EnvironmentDefinitions.ValidateRuntime(resolved.Document).IsValid);
        Assert.True(CustomTaskDefinitions.ValidateRuntime(resolved.Document).IsValid);
        await Orchestrator(store, agent).AdvanceAsync(workflow, default);
        Assert.True(workflow.Status == WorkflowStatus.Completed, workflow.FailureReason + " " + string.Join("; ", (await store.ListLogsAsync(workflow.Id, default)).Select(log => log.Message)));
        var task = Assert.Single(agent.Tasks);
        Assert.Contains("Check the evidence", task.Prompt);
        Assert.Contains("Inspect original plan", task.Prompt);
        var environment = Assert.IsType<EnvironmentSnapshot>(task.EnvironmentSnapshot);
        Assert.Equal("Always", environment.Configuration.Image!.PullPolicy);
        Assert.Equal(["registry-auth"], environment.Configuration.Image.PullSecretNames);
        Assert.Equal(50, environment.Configuration.Runtime!.TimeoutLimitSeconds);
        Assert.Single(environment.Configuration.Tools); Assert.Single(environment.Configuration.McpServers);
        var run = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default));
        Assert.Contains("Checked", run.StructuredOutputsJson);
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
        public bool Succeeded = true; public bool FinalResponse = true;
        public string? Immediate; public bool Uncertain; public bool Permanent; public bool PollFailure; public string? PollOutput;
        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken token)
        {
            Tasks.Add(task); token.ThrowIfCancellationRequested();
            if (Uncertain) { Uncertain = false; throw new AgentLaunchUncertainException("lost response", new HttpRequestException()); }
            if (Permanent) throw new InvalidOperationException("bad configuration");
            var id = task.ExecutionAttemptId!.Value.ToString("N");
            return Task.FromResult(new AgentRunStartResult(id, Immediate is null ? null : new(Succeeded, id, Immediate, Succeeded ? null : "Agent execution failed", FinalResponse)));
        }
        public Task<AgentRunResult?> TryGetResultAsync(string id, CancellationToken token)
        { if (PollFailure) throw new HttpRequestException("temporary"); return Task.FromResult<AgentRunResult?>(PollOutput is null ? null : new(true, id, PollOutput, null)); }
    }
}
