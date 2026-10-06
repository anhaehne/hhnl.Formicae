using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class ScriptTaskOrchestratorTests
{
    [Fact]
    public async Task Script_preserves_output_exit_code_and_settings_without_ai_or_git_side_effects()
    {
        var profile = new EnvironmentSnapshot("script-env", 2, "Script tools", "", new() { Image = new("worker:custom"), Tools = [new("curl", "echo installed")] });
        var reference = new WorkflowSecretReference("DEPLOY_TOKEN", "deploy-secret", "token");
        var (store, workflow) = await Setup([Script() with { EnvironmentId = profile.Id, EnvironmentSnapshot = profile,
            Capabilities = ["tool:curl"], SecretReferences = [reference] }]);
        var runner = new Runner { Result = new(true, "worker", "hello\n", null, ExitCode: 0) };
        await Orchestrator(store, runner).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status); Assert.Equal("original plan", workflow.PlanArtifact);
        var task = Assert.Single(runner.Tasks); Assert.Equal(TaskRunKind.Script, task.Kind); Assert.Null(task.Model); Assert.Null(task.AiSettingsId);
        Assert.Equal(profile.Id, task.EnvironmentSnapshot!.Id); Assert.Equal(profile.Revision, task.EnvironmentSnapshot.Revision);
        Assert.Equal(profile.Configuration.Image!.Reference, task.EnvironmentSnapshot.Configuration.Image!.Reference); Assert.Equal(new[] { "tool:curl" }, task.Capabilities); Assert.Equal(reference, Assert.Single(task.SecretReferences!));
        Assert.Equal(53, task.TimeoutSeconds); Assert.Equal("echo hello", task.Script!.Script); Assert.NotNull(task.ExecutionAttemptId);
        var run = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default));
        Assert.Equal(0, run.ExitCode); Assert.Equal("hello\n", run.Output); Assert.Equal("hello\n", run.ToResponse().StructuredOutputs!["output"].GetString());
        var settings = Assert.Single(await store.ListEventsAsync(workflow.Id, default), evt => evt.Type == "AgentSettingsResolved");
        using var audit = JsonDocument.Parse(settings.DetailsJson!); Assert.False(audit.RootElement.TryGetProperty("aiSettingsId", out _));
    }

    [Fact]
    public async Task Script_failure_retry_archives_real_exit_code_and_preserves_script_snapshot()
    {
        var (store, workflow) = await Setup(); var runner = new Runner { Result = new(false, "worker", "failed output", "exit 7", ExitCode: 7) };
        await Orchestrator(store, runner).AdvanceAsync(workflow, default); Assert.Equal(WorkflowStatus.Failed, workflow.Status);
        var run = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default)); var previous = run.ExecutionAttemptId;
        await new WorkflowService(store).RetryWorkflowAsync(workflow.Id, default);
        Assert.Null(run.ExitCode); Assert.NotEqual(previous, run.ExecutionAttemptId);
        var archived = Assert.Single(await store.ListTaskRunAttemptsAsync(workflow.Id, default)); Assert.Equal(7, archived.ExitCode); Assert.Equal("failed output", archived.Output);
        runner.Result = new(true, "worker2", "recovered", null, ExitCode: 0);
        await Orchestrator(store, runner).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status); Assert.Equal(runner.Tasks[0].Script, runner.Tasks[1].Script);
    }

    [Fact]
    public async Task Script_lost_launch_response_reuses_attempt_after_restart_and_paused_workflow_only_polls()
    {
        var (store, workflow) = await Setup(); var runner = new Runner { Uncertain = true };
        await Orchestrator(store, runner).AdvanceAsync(workflow, default);
        var run = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default)); var attempt = run.ExecutionAttemptId;
        await Orchestrator(store, runner).AdvanceAsync(workflow, default); Assert.Equal(attempt, runner.Tasks.Last().ExecutionAttemptId);
        workflow.IsPaused = true; runner.Polled = new(true, "worker", "finished", null, ExitCode: 0);
        await Orchestrator(store, runner).AdvanceAsync(workflow, default);
        Assert.Equal(2, runner.Tasks.Count); Assert.Equal(TaskRunStatus.Succeeded, run.Status); Assert.Equal(WorkflowStatus.Completed, workflow.Status);
    }

    [Fact]
    public async Task Script_loop_iterations_have_distinct_attempts_without_unidentified_legacy_reuse()
    {
        var steps = new[] { new WorkflowDefinitionStep("loop", "builtins.loop", "finish", Loop: new("script", 2, 2)),
            Script("script", "loop") with { NextStepPort = "return" }, Script("finish") };
        var (store, workflow) = await Setup(steps, "loop"); var runner = new Runner { Result = new(true, "worker", "ok", null, ExitCode: 0) };
        await store.UpsertTaskRunAsync(new() { WorkflowId = workflow.Id, Kind = TaskRunKind.Script, Status = TaskRunStatus.Succeeded, Output = "legacy" }, default);
        for (var index = 0; index < 6; index++) await Orchestrator(store, runner).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        var runs = (await store.ListTaskRunsAsync(workflow.Id, default)).Where(run => run.DefinitionStepId != "").ToArray();
        Assert.Equal(3, runs.Length); Assert.Equal(3, runs.Select(run => run.ExecutionAttemptId).Distinct().Count());
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("")]
    public async Task Script_output_binding_pins_source_attempt_and_consumer_retry_does_not_recapture(string output)
    {
        var snapshot = new CustomTaskSnapshot("task", 1, "Consumer", "", "Consume {{input.value}}", [new("value", "string", true)], new());
        var consumer = new WorkflowDefinitionStep("consumer", CustomTaskDefinitions.Uses, CustomTask: new("task", Snapshot: snapshot,
            Bindings: new Dictionary<string, CustomTaskInputBinding> { ["value"] = new("script", "output") }));
        var (store, workflow) = await Setup([Script("script", "consumer"), consumer]);
        var runner = new Runner { Result = new(true, "worker", output, null, ExitCode: 0) };
        await Orchestrator(store, runner).AdvanceAsync(workflow, default); var source = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default));
        runner.Result = new(false, "consumer-worker", "failed", "error");
        await Orchestrator(store, runner).AdvanceAsync(workflow, default);
        var run = (await store.ListTaskRunsAsync(workflow.Id, default)).Single(run => run.DefinitionStepId == "consumer");
        Assert.Equal(source.ExecutionAttemptId, run.ToResponse().CustomTaskExecution!.Provenance!["value"].ExecutionAttemptId);
        var prepared = run.CustomTaskExecutionJson; await new WorkflowService(store).RetryWorkflowAsync(workflow.Id, default);
        source.StructuredOutputsJson = "{\"output\":\"changed\"}"; runner.Result = new(true, "consumer-worker2", "done", null);
        await Orchestrator(store, runner).AdvanceAsync(workflow, default);
        Assert.Equal(prepared, run.CustomTaskExecutionJson); Assert.Equal(WorkflowStatus.Completed, workflow.Status);
    }

    [Fact]
    public async Task Oversized_script_output_is_retained_but_cannot_overflow_consumer_input()
    {
        var snapshot = new CustomTaskSnapshot("task", 1, "Consumer", "", "Consume {{input.value}}", [new("value", "string", true)], new());
        var consumer = new WorkflowDefinitionStep("consumer", CustomTaskDefinitions.Uses, CustomTask: new("task", Snapshot: snapshot,
            Bindings: new Dictionary<string, CustomTaskInputBinding> { ["value"] = new("script", "output") }));
        var (store, workflow) = await Setup([Script("script", "consumer"), consumer]);
        var output = new string('x', 16001); var runner = new Runner { Result = new(true, "worker", output, null, ExitCode: 0) };
        await Orchestrator(store, runner).AdvanceAsync(workflow, default);
        await Orchestrator(store, runner).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status); Assert.Single(runner.Tasks);
        var source = (await store.ListTaskRunsAsync(workflow.Id, default)).Single(run => run.DefinitionStepId == "script");
        Assert.Equal(output, source.Output); Assert.Equal(TaskRunStatus.Succeeded, source.Status);
        Assert.Contains("output", workflow.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Paused_script_reattaches_uncertain_launch_without_starting_another_worker()
    {
        var (store, workflow) = await Setup(); var runner = new Runner { Uncertain = true, ResolveId = true };
        await Orchestrator(store, runner).AdvanceAsync(workflow, default);
        var run = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default)); Assert.Null(run.ExternalId);
        workflow.IsPaused = true; runner.Polled = new(true, "worker", "reattached", null, ExitCode: 0);
        await Orchestrator(store, runner).AdvanceAsync(workflow, default);
        Assert.Single(runner.Tasks); Assert.Equal(TaskRunStatus.Succeeded, run.Status); Assert.Equal("worker", run.ExternalId);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status); Assert.Equal(0, run.ExitCode);
    }

    [Theory]
    [InlineData("inherit", 2)]
    [InlineData("none", 0)]
    [InlineData("selected", 1)]
    public async Task Execution_audit_lists_only_enabled_tools_and_mcp_servers(string selection, int installedTools)
    {
        var profile = new EnvironmentSnapshot("script-env", 1, "Script resources", "", new()
        {
            Tools = [new("curl", "echo curl"), new("jq", "echo jq")], McpServers = [new("docs", Command: "docs")]
        });
        IReadOnlyList<string>? capabilities = selection switch { "none" => [], "selected" => ["tool:jq"], _ => null };
        var (store, workflow) = await Setup([Script() with { EnvironmentId = profile.Id, EnvironmentSnapshot = profile, Capabilities = capabilities }]);
        var runner = new Runner { Result = new(true, "worker", "done", null, ExitCode: 0) };
        await Orchestrator(store, runner).AdvanceAsync(workflow, default);
        var settings = Assert.Single(await store.ListEventsAsync(workflow.Id, default), evt => evt.Type == "AgentSettingsResolved");
        using var audit = JsonDocument.Parse(settings.DetailsJson!);
        var environment = audit.RootElement.GetProperty("environment");
        Assert.Equal(installedTools, environment.GetProperty("tools").GetArrayLength());
        Assert.Equal(0, environment.GetProperty("mcpServers").GetArrayLength());
        if (selection == "selected") Assert.Equal("jq", environment.GetProperty("tools")[0].GetString());
        Assert.Equal(2, Assert.Single(runner.Tasks).EnvironmentSnapshot!.Configuration.Tools.Count);
    }

    private static WorkflowDefinitionStep Script(string id = "script", string? next = null) => new(id, WorkflowExecutionExtensions.ScriptUses, next, Script: new("echo hello", TimeoutSeconds: 53));
    private static async Task<(InMemoryWorkflowStore, Workflow)> Setup(IReadOnlyList<WorkflowDefinitionStep>? steps = null, string start = "script")
    {
        var store = new InMemoryWorkflowStore(); var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, start, steps ?? [Script()]);
        Assert.True(new WorkflowDefinitionValidator().Validate(document).IsValid); Assert.True(CustomTaskDefinitions.ValidateRuntime(document).IsValid);
        var definition = await store.CreateWorkflowDefinitionAsync(new() { Name = "Script" }, default);
        var version = await store.CreateWorkflowDefinitionVersionAsync(new() { WorkflowDefinitionId = definition.Id, Version = 1, DslSchemaVersion = document.Schema, DefinitionJson = WorkflowDefinitionJson.Serialize(document), IsEnabled = true }, default);
        return (store, await store.CreateWorkflowAsync(new() { IssueUrl = "https://example.test/issues/1", RepositoryUrl = "https://example.test/repo", WorkflowDefinitionId = definition.Id, WorkflowDefinitionVersionId = version.Id,
            CurrentDefinitionStepId = WorkflowNodeDefinitions.Normalize(document).StartStepId, PlanArtifact = "original plan" }, default));
    }
    private static WorkflowOrchestrator Orchestrator(IWorkflowStore store, Runner runner) => new(store,
        System.Reflection.DispatchProxy.Create<IWorkItemProvider, Forbidden>(), System.Reflection.DispatchProxy.Create<ISourceControlProvider, Forbidden>(), runner, System.Reflection.DispatchProxy.Create<IPromptRenderer, Forbidden>());
    public class Forbidden : System.Reflection.DispatchProxy
    { protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) => throw new InvalidOperationException("Unexpected side effect: " + method!.Name); }
    private sealed class Runner : IAgentRunner
    {
        public List<AgentTask> Tasks = []; public AgentRunResult? Result; public AgentRunResult? Polled; public bool Uncertain; public bool ResolveId;
        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken token)
        { Tasks.Add(task); if (Uncertain) { Uncertain = false; throw new AgentLaunchUncertainException("lost", new HttpRequestException()); } return Task.FromResult(new AgentRunStartResult("worker", Result)); }
        public string? ResolveExternalId(Guid workflowId, TaskRunKind kind, Guid? executionAttemptId) => ResolveId ? "worker" : null;
        public Task<AgentRunResult?> TryGetResultAsync(string id, CancellationToken token) => Task.FromResult(Polled);
    }
}
