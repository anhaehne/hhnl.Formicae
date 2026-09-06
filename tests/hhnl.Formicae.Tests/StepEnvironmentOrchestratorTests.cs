using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class StepEnvironmentOrchestratorTests
{
    [Fact]
    public async Task Ordinary_and_custom_nodes_select_independent_profiles_and_direct_pr_has_no_environment()
    {
        var fixture = await SetupAsync((a, b) => [
            Ai("a", "builtins.plan", "b"), Ai("b", "builtins.plan", "implement", b),
            Ai("implement", "builtins.implement", "pr", "default"), new("pr", "builtins.create-pull-request", "review"),
            Ai("review", "builtins.address-comments", "custom", b), Custom("custom") ], "a");
        await fixture.DeleteCatalogAsync();
        var agent = new Agent();
        for (var i = 0; i < 10; i++) await fixture.Orchestrator(agent).AdvanceAsync(fixture.Workflow, default);
        Assert.Equal(WorkflowStatus.Completed, fixture.Workflow.Status);
        Assert.Equal(new[] { fixture.A.Id, fixture.B.Id, "default", fixture.B.Id, fixture.A.Id }, agent.Tasks.Select(task => task.EnvironmentSnapshot!.Id));
        Assert.Equal(new int?[] { 30, 60, null, 60, 30 }, agent.Tasks.Select(task => task.EnvironmentSnapshot!.Configuration.Runtime?.TimeoutLimitSeconds));
        Assert.All(agent.Tasks, AssertIdentityAndPersona);
        Assert.Equal(43, agent.Tasks[^1].TimeoutSeconds); Assert.NotEmpty(agent.Tasks[^1].ContextFiles!);
        var runs = await fixture.Store.ListTaskRunsAsync(fixture.Workflow.Id, default);
        var pr = Assert.Single(runs, run => run.Kind == TaskRunKind.CreatePullRequest);
        Assert.DoesNotContain(await fixture.Store.ListEventsAsync(fixture.Workflow.Id, default), item => item.Type == "AgentSettingsResolved" && item.TaskRunId == pr.Id);
        Assert.Null(EnvironmentDefinitions.ResolveForTask(fixture.Document, fixture.Document.Steps.Single(step => step.Id == "pr")));
        await AssertAuditsAsync(fixture, new Dictionary<string, string> { ["a"] = fixture.A.Id, ["b"] = fixture.B.Id,
            ["implement"] = "default", ["review"] = fixture.B.Id, ["custom"] = fixture.A.Id });
    }

    [Fact]
    public async Task Parallel_branches_keep_different_profiles_and_attempt_identity_after_uncertain_launch_and_catalog_deletion()
    {
        var fixture = await SetupAsync((a, b) => [
            new("group", WorkflowParallelDefinitions.Uses, "finish", Parallel: new(["a", "b"])),
            Ai("a", "builtins.plan", "group", b) with { NextStepPort = "join" },
            Ai("b", "builtins.plan", "group", "default") with { NextStepPort = "join" }, Custom("finish") ], "group");
        var agent = new Agent { UncertainFirst = true };
        await fixture.Orchestrator(agent).AdvanceAsync(fixture.Workflow, default);
        await fixture.DeleteCatalogAsync();
        for (var i = 0; i < 5; i++) await fixture.Orchestrator(agent).AdvanceAsync(fixture.Workflow, default);
        Assert.Equal(WorkflowStatus.Completed, fixture.Workflow.Status);
        var retried = agent.Tasks.Where(task => task.Prompt.StartsWith("a:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, retried.Length); Assert.Equal(retried[0].ExecutionAttemptId, retried[1].ExecutionAttemptId);
        Assert.NotNull(retried[0].ExecutionAttemptId); Assert.All(retried, task => Assert.Equal(fixture.B.Id, task.EnvironmentSnapshot!.Id));
        Assert.Equal(retried[0].Prompt, retried[1].Prompt); Assert.Equal(1, retried[1].EnvironmentSnapshot!.Revision);
        Assert.Equal("default", agent.Tasks.Single(task => task.Prompt.StartsWith("b:", StringComparison.Ordinal)).EnvironmentSnapshot!.Id);
        Assert.All(agent.Tasks, AssertIdentityAndPersona);
        await AssertAuditsAsync(fixture, new Dictionary<string, string> { ["a"] = fixture.B.Id, ["b"] = "default", ["finish"] = fixture.A.Id });
    }

    [Fact]
    public async Task Loop_iterations_keep_override_and_exit_optout_after_profiles_are_deleted()
    {
        var fixture = await SetupAsync((a, b) => [
            new("loop", WorkflowNodeDefinitions.LoopUses, "exit", Loop: new("body", 2, 2)),
            Custom("body", "loop", b) with { NextStepPort = "return" }, Custom("exit", environment: "default") ], "loop");
        await fixture.DeleteCatalogAsync(); var agent = new Agent();
        for (var i = 0; i < 6; i++) await fixture.Orchestrator(agent).AdvanceAsync(fixture.Workflow, default);
        Assert.Equal(WorkflowStatus.Completed, fixture.Workflow.Status);
        Assert.Equal(new[] { fixture.B.Id, fixture.B.Id, "default" }, agent.Tasks.Select(task => task.EnvironmentSnapshot!.Id));
        var runs = await fixture.Store.ListTaskRunsAsync(fixture.Workflow.Id, default);
        Assert.Equal(new int?[] { 1, 2 }, runs.Where(run => run.DefinitionStepId == "body").Select(run => run.LoopIteration).Order());
        Assert.Equal(3, runs.Select(run => run.ExecutionAttemptId).Distinct().Count());
        Assert.All(agent.Tasks, AssertIdentityAndPersona);
        await AssertAuditsAsync(fixture, new Dictionary<string, string> { ["body"] = fixture.B.Id, ["exit"] = "default" });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_custom_retry_preserves_pinned_step_profile_and_prepared_context(bool optOut)
    {
        var fixture = await SetupAsync((a, b) => [Custom("custom", environment: optOut ? "default" : b)], "custom");
        var agent = new Agent { FailFirst = true };
        await fixture.Orchestrator(agent).AdvanceAsync(fixture.Workflow, default);
        Assert.Equal(WorkflowStatus.Failed, fixture.Workflow.Status);
        var run = Assert.Single(await fixture.Store.ListTaskRunsAsync(fixture.Workflow.Id, default)); var payload = run.CustomTaskExecutionJson;
        await fixture.DeleteCatalogAsync();
        await new WorkflowService(fixture.Store).RetryWorkflowAsync(fixture.Workflow.Id, default);
        fixture.Workflow.PlanArtifact = "mutable runtime change";
        await fixture.Orchestrator(agent).AdvanceAsync(fixture.Workflow, default);
        Assert.Equal(WorkflowStatus.Completed, fixture.Workflow.Status); Assert.Equal(2, agent.Tasks.Count);
        Assert.Equal(agent.Tasks[0].Prompt, agent.Tasks[1].Prompt); Assert.NotEqual(agent.Tasks[0].ExecutionAttemptId, agent.Tasks[1].ExecutionAttemptId);
        Assert.All(agent.Tasks, task => Assert.Equal(optOut ? "default" : fixture.B.Id, task.EnvironmentSnapshot!.Id));
        Assert.All(agent.Tasks, AssertIdentityAndPersona);
        Assert.Equal(payload, (await fixture.Store.ListTaskRunsAsync(fixture.Workflow.Id, default))[0].CustomTaskExecutionJson);
    }

    [Fact]
    public async Task Invalid_persisted_step_reference_fails_before_any_agent_launch()
    {
        var fixture = await SetupAsync((a, b) => [Custom("custom", environment: b)], "custom");
        var invalid = fixture.Document with { Steps = [fixture.Document.Steps[0] with { EnvironmentId = "unresolved", EnvironmentSnapshot = null }] };
        var version = await fixture.Store.CreateWorkflowDefinitionVersionAsync(new() { WorkflowDefinitionId = fixture.Workflow.WorkflowDefinitionId!.Value,
            Version = 2, DslSchemaVersion = invalid.Schema, DefinitionJson = WorkflowDefinitionJson.Serialize(invalid), IsEnabled = true }, default);
        fixture.Workflow.WorkflowDefinitionVersionId = version.Id;
        var agent = new Agent(); await fixture.Orchestrator(agent).AdvanceAsync(fixture.Workflow, default);
        Assert.Equal(WorkflowStatus.Failed, fixture.Workflow.Status); Assert.Empty(agent.Tasks);
        Assert.Contains("environment", fixture.Workflow.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertIdentityAndPersona(AgentTask task)
    {
        Assert.Equal("node-model", task.Model); Assert.Equal("node-ai", task.AiSettingsId);
        Assert.Contains("pinned persona guidance", task.Prompt);
        Assert.Equal(1, task.Prompt.Split("## Persona guidance", StringSplitOptions.None).Length - 1);
    }
    private static WorkflowDefinitionStep Ai(string id, string uses, string? next = null, string? environment = null)
        => new(id, uses, next, Model: "node-model", AiSettingsId: "node-ai", EnvironmentId: environment);
    private static WorkflowDefinitionStep Custom(string id, string? next = null, string? environment = null)
        => Ai(id, CustomTaskDefinitions.Uses, next, environment) with { CustomTask = new("task", Snapshot:
            new("task", 1, "Custom", "", "Review {{workflow.planArtifact}}", [], new(TimeoutSeconds: 43))) };
    private static async Task<Fixture> SetupAsync(Func<string, string, IReadOnlyList<WorkflowDefinitionStep>> build, string start)
    {
        var catalog = new EnvironmentService(new InMemoryEnvironmentStore());
        var a = await catalog.CreateAsync(new("Workflow profile", Configuration: new() { Runtime = new(30) }), default);
        var b = await catalog.CreateAsync(new("Node profile", Configuration: new() { Runtime = new(60) }), default);
        var personas = new PersonaService(new InMemoryPersonaStore()); var persona = await personas.CreateAsync(new("Guide", "pinned persona guidance"), default);
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, start, build(a.Id, b.Id),
            DefaultPersonaId: persona.Id, DefaultEnvironmentId: a.Id);
        document = (await PersonaDefinitions.ResolveAsync(document, personas, default)).Document;
        var resolved = await EnvironmentDefinitions.ResolveAsync(document, catalog, default);
        Assert.True(resolved.Validation.IsValid, string.Join("; ", resolved.Validation.Errors.Select(error => error.Message))); document = resolved.Document;
        var store = new InMemoryWorkflowStore(); var definition = await store.CreateWorkflowDefinitionAsync(new() { Name = "Step environments" }, default);
        var version = await store.CreateWorkflowDefinitionVersionAsync(new() { WorkflowDefinitionId = definition.Id, Version = 1,
            DslSchemaVersion = document.Schema, DefinitionJson = WorkflowDefinitionJson.Serialize(document), IsEnabled = true }, default);
        var workflow = await store.CreateWorkflowAsync(new() { IssueUrl = "https://example.test/issues/1", RepositoryUrl = "https://example.test/repo",
            WorkflowDefinitionId = definition.Id, WorkflowDefinitionVersionId = version.Id,
            CurrentDefinitionStepId = WorkflowNodeDefinitions.Normalize(document).StartStepId, Status = WorkflowStatus.Planning, PlanArtifact = "original context" }, default);
        return new(store, workflow, document, catalog, a, b);
    }
    private static async Task AssertAuditsAsync(Fixture fixture, IReadOnlyDictionary<string, string> expected)
    {
        var runs = (await fixture.Store.ListTaskRunsAsync(fixture.Workflow.Id, default)).ToDictionary(run => run.Id);
        var events = (await fixture.Store.ListEventsAsync(fixture.Workflow.Id, default)).Where(item => item.Type == "AgentSettingsResolved").ToArray();
        Assert.NotEmpty(events);
        Assert.All(events, item =>
        {
            var run = runs[item.TaskRunId!.Value]; using var json = JsonDocument.Parse(item.DetailsJson!);
            Assert.Equal(expected[run.DefinitionStepId], json.RootElement.GetProperty("environment").GetProperty("id").GetString());
        });
    }
    private sealed record Fixture(InMemoryWorkflowStore Store, Workflow Workflow, WorkflowDefinitionDocument Document,
        EnvironmentService Catalog, EnvironmentResponse A, EnvironmentResponse B)
    {
        public async Task DeleteCatalogAsync() { await Catalog.DeleteAsync(A.Id, 1, default); await Catalog.DeleteAsync(B.Id, 1, default); }
        public WorkflowOrchestrator Orchestrator(Agent agent) => new(Store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new Prompt());
    }
    private sealed class Prompt : IPromptRenderer
    {
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? issue, CancellationToken token) => Task.FromResult(workflow.CurrentDefinitionStepId + ": prompt");
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? issue, IReadOnlyList<PullRequestComment> comments, CancellationToken token) => RenderAsync(kind, workflow, issue, token);
    }
    private sealed class Agent : IAgentRunner
    {
        public List<AgentTask> Tasks = []; public bool UncertainFirst; public bool FailFirst;
        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken token)
        {
            Tasks.Add(task);
            if (UncertainFirst) { UncertainFirst = false; throw new AgentLaunchUncertainException("lost response", new HttpRequestException()); }
            var succeeded = !FailFirst; FailFirst = false; var id = task.ExecutionAttemptId?.ToString("N") ?? Tasks.Count.ToString();
            return Task.FromResult(new AgentRunStartResult(id, new(succeeded, id, "successful task output", succeeded ? null : "retry me")));
        }
        public Task<AgentRunResult?> TryGetResultAsync(string id, CancellationToken token) => Task.FromResult<AgentRunResult?>(null);
    }
}
