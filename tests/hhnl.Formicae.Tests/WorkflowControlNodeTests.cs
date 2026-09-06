using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Infrastructure.Prompts;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowControlNodeTests
{
    private static WorkflowDefinitionDocument Document() => new(DefaultWorkflowDefinitions.V1Alpha3Schema, "repeat", [
        new("repeat", WorkflowNodeDefinitions.LoopUses, "finish", Loop: new("plan", 2, 3, 60)),
        new("plan", "builtins.plan", "repeat", AiSettingsId: "codex", Model: "chosen", NextStepPort: "return"),
        new("finish", "builtins.implement"),
        new("label", WorkflowNodeDefinitions.TriggerUses, "repeat", Trigger: new(WorkflowTriggerType.DevOpsIssueLabel, false, [], "ready"))
    ]);

    [Fact]
    public void Nodes_compile_to_existing_iteration_plan_without_losing_task_settings()
    {
        var document = WorkflowDefinitionJson.Deserialize(WorkflowDefinitionJson.Serialize(Document()))!;
        Assert.True(new WorkflowDefinitionValidator().Validate(document).IsValid);
        var plan = WorkflowNodeDefinitions.Normalize(document);
        Assert.Equal("plan", plan.StartStepId);
        Assert.Equal("plan", plan.Triggers![0].NextStepId);
        Assert.Equal("repeat", Assert.Single(plan.Loops!).Id);
        Assert.Equal(["plan"], plan.Loops![0].BodyStepIds);
        Assert.Equal("finish", plan.Loops[0].ExitStepId);
        Assert.Equal("plan", plan.Steps[0].NextStepId);
        Assert.Equal("codex", plan.Steps[0].AiSettingsId);
        Assert.Equal("chosen", plan.Steps[0].Model);
        Assert.Equal(4, document.Steps.Count);
    }

    [Theory]
    [InlineData("missing-body")]
    [InlineData("no-return")]
    [InlineData("wrong-return")]
    [InlineData("nested")]
    [InlineData("overlap")]
    [InlineData("entry-body")]
    [InlineData("trigger-input")]
    [InlineData("bounds")]
    [InlineData("timeout")]
    [InlineData("cycle")]
    [InlineData("disconnected")]
    [InlineData("duplicate")]
    [InlineData("mixed-schema")]
    public void Invalid_node_connections_and_guards_are_rejected(string scenario)
    {
        var document = Document();
        var steps = document.Steps.ToList();
        switch (scenario)
        {
            case "missing-body": steps[0] = steps[0] with { Loop = steps[0].Loop! with { BodyStepId = "unknown" } }; break;
            case "no-return": steps[1] = steps[1] with { NextStepId = "finish", NextStepPort = null }; break;
            case "wrong-return": steps[2] = steps[2] with { NextStepId = "repeat", NextStepPort = "return" }; break;
            case "nested": steps[0] = steps[0] with { Loop = steps[0].Loop! with { BodyStepId = "repeat" } }; break;
            case "overlap": steps.Add(steps[0] with { Id = "second" }); break;
            case "entry-body": steps[3] = steps[3] with { NextStepId = "plan" }; break;
            case "trigger-input": steps[2] = steps[2] with { NextStepId = "label" }; break;
            case "bounds": steps[0] = steps[0] with { Loop = steps[0].Loop! with { RepeatCount = 4 } }; break;
            case "timeout": steps[0] = steps[0] with { Loop = steps[0].Loop! with { TimeoutSeconds = 0 } }; break;
            case "cycle": steps[2] = steps[2] with { NextStepId = "repeat" }; break;
            case "disconnected": steps.Add(new("unused", "builtins.plan")); break;
            case "duplicate": steps.Add(steps[1]); break;
            case "mixed-schema": document = document with { Loops = [new("old", ["plan"], 1, 1, "finish")] }; break;
        }
        Assert.False(new WorkflowDefinitionValidator().Validate(document with { Steps = steps }).IsValid);
    }

    [Fact]
    public void Legacy_documents_are_not_rewritten_by_normalization()
    {
        var legacy = DefaultWorkflowDefinitions.CreateMvpDocument();
        Assert.Same(legacy, WorkflowNodeDefinitions.Normalize(legacy));
        var v2 = legacy with { Schema = DefaultWorkflowDefinitions.V1Alpha2Schema };
        Assert.Same(v2, WorkflowNodeDefinitions.Normalize(v2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Loop_nodes_execute_and_retry_using_persisted_iterations(bool retry)
    {
        var store = new InMemoryWorkflowStore();
        var service = new WorkflowDefinitionService(store, new());
        var definition = await service.CreateAsync(new("Nodes"), default);
        var version = await service.CreateVersionAsync(definition.Id, new(null, true, false, Document()), default);
        var workflows = new WorkflowService(store, workflowDefinitions: service);
        var started = await workflows.StartGitHubIssueWorkflowAsync(new("https://example.com/issues/1", "https://example.com/repo", null, null, WorkflowDefinitionId: definition.Id, WorkflowDefinitionVersionId: version.Id), default);
        var workflow = (await store.GetWorkflowAsync(started.WorkflowId, default))!;
        var agent = new RecordingAgent { FailNext = retry };
        WorkflowOrchestrator Restart() => new(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new FilePromptRenderer());
        if (retry)
        {
            await Restart().AdvanceAsync(workflow, default);
            Assert.Equal(WorkflowStatus.Failed, workflow.Status);
            await workflows.RetryWorkflowAsync(workflow.Id, default);
        }
        for (var i = 0; i < 3; i++) await Restart().AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal(2, (await store.ListLoopIterationsAsync(workflow.Id, default)).Count);
        Assert.All(agent.Tasks.Where(t => t.Kind == TaskRunKind.Plan), t => Assert.Equal("chosen", t.Model));
        Assert.DoesNotContain(await store.ListTaskRunsAsync(workflow.Id, default), r => r.DefinitionStepId is "repeat" or "label");
        Assert.Equal(WorkflowDefinitionJson.Serialize(version.Definition), WorkflowDefinitionJson.Serialize((await service.GetAsync(definition.Id, default))!.Versions[0].Definition));
    }

    [Fact]
    public async Task Trigger_nodes_start_at_their_own_targets_and_deduplicate_deliveries()
    {
        var store = new InMemoryWorkflowStore();
        var integrations = new InMemoryDevOpsIntegrationStore();
        var integration = await integrations.CreateAsync(new DevOpsIntegration { ProviderType = DevOpsProviderType.GitHub, DisplayName = "test" }, default);
        var repository = await integrations.AddRepositoryAsync(new ConnectedRepository { DevOpsIntegrationId = integration.Id, Owner = "acme", Name = "repo", RepositoryUrl = "https://example.com/repo", DefaultBranch = "main" }, default);
        var definitions = new WorkflowDefinitionService(store, new(), integrations);
        var definition = await definitions.CreateAsync(new("Entries"), default);
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "manual", [
            new("manual", "builtins.plan"), new("event-plan", "builtins.plan"),
            new("label", WorkflowNodeDefinitions.TriggerUses, "event-plan", Trigger: new(WorkflowTriggerType.DevOpsIssueLabel, true, [repository.Id], "ready", "develop", "trigger-model"))
        ]);
        var version = await definitions.CreateVersionAsync(definition.Id, new(null, true, false, document), default);
        var workflows = new WorkflowService(store, workflowDefinitions: definitions);
        var triggers = new WorkflowTriggerService(store, integrations, workflows);
        var evt = new DevOpsIssueLabelTriggerEvent(DevOpsProviderType.GitHub, "delivery", "issues", "labeled", repository.RepositoryUrl, "https://example.com/issues/2", "ready", "acme/repo");
        var id = Assert.Single(await triggers.HandleIssueLabelEventAsync(evt, default));
        var run = (await store.GetWorkflowAsync(id, default))!;
        Assert.Equal("event-plan", run.CurrentDefinitionStepId);
        Assert.Equal("develop", run.BaseBranch);
        Assert.Equal("trigger-model", run.Model);
        Assert.Equal("label", Assert.Single(await store.ListTriggerEventsAsync(id, default)).TriggerId);
        Assert.Empty(await triggers.HandleIssueLabelEventAsync(evt with { IssueUrl = "https://example.com/issues/3" }, default));
        var manual = await workflows.StartGitHubIssueWorkflowAsync(new("https://example.com/issues/4", repository.RepositoryUrl, null, null, WorkflowDefinitionId: definition.Id, WorkflowDefinitionVersionId: version.Id), default);
        Assert.Equal("manual", (await store.GetWorkflowAsync(manual.WorkflowId, default))!.CurrentDefinitionStepId);
    }

    private sealed class RecordingAgent : IAgentRunner
    {
        public bool FailNext;
        public List<AgentTask> Tasks = [];
        private readonly FakeAgentRunner inner = new();
        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken token)
        {
            Tasks.Add(task);
            if (FailNext) { FailNext = false; return Task.FromResult(new AgentRunStartResult("failed", new(false, "failed", "", "fixture"))); }
            return inner.StartAsync(task, token);
        }
        public Task<AgentRunResult?> TryGetResultAsync(string id, CancellationToken token) => inner.TryGetResultAsync(id, token);
    }

    [Fact]
    public async Task Active_node_loop_resumes_after_restart_without_duplicate_agent_jobs()
    {
        var (store, workflow) = await SetupAsync();
        var agent = new DeferredAgent();
        WorkflowOrchestrator Restart() => new(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new FilePromptRenderer());
        await Restart().AdvanceAsync(workflow, default);
        var first = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default));
        agent.Results[first.ExternalId!] = new(true, first.ExternalId!, "plan", null);
        await Restart().AdvanceAsync(workflow, default);
        await Restart().AdvanceAsync(workflow, default);
        Assert.Equal(2, agent.Started);
        var runs = await store.ListTaskRunsAsync(workflow.Id, default);
        Assert.Single(runs, run => run.LoopIteration == 1 && run.Status == TaskRunStatus.Succeeded);
        Assert.Single(runs, run => run.LoopIteration == 2 && run.Status == TaskRunStatus.Running);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Node_loop_reuses_timeout_and_max_iteration_runtime_guards(bool exhaustedMaximum)
    {
        var (store, workflow) = await SetupAsync();
        var clock = new Clock();
        var orchestrator = new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), new FakeAgentRunner(), new FilePromptRenderer(), clock: clock);
        if (exhaustedMaximum)
        {
            for (var i = 1; i <= 3; i++) await store.UpsertLoopIterationAsync(new WorkflowLoopIteration {
                WorkflowId = workflow.Id, LoopId = "repeat", IterationNumber = i, StartedAt = clock.UtcNow, Outcome = WorkflowLoopIterationOutcome.Succeeded
            }, default);
        }
        else
        {
            await orchestrator.AdvanceAsync(workflow, default);
            clock.UtcNow = clock.UtcNow.AddSeconds(61);
        }
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status);
        Assert.StartsWith(exhaustedMaximum ? "LOOP_MAX_ITERATIONS_EXCEEDED" : "LOOP_TIMEOUT_EXCEEDED", workflow.FailureReason);
    }

    private static async Task<(InMemoryWorkflowStore, Workflow)> SetupAsync()
    {
        var store = new InMemoryWorkflowStore();
        var definitions = new WorkflowDefinitionService(store, new());
        var definition = await definitions.CreateAsync(new("Control"), default);
        var version = await definitions.CreateVersionAsync(definition.Id, new(null, true, false, Document()), default);
        var summary = await new WorkflowService(store, workflowDefinitions: definitions).StartGitHubIssueWorkflowAsync(
            new("https://example.com/issues/1", "https://example.com/repo", null, null, definition.Id, version.Id), default);
        return (store, (await store.GetWorkflowAsync(summary.WorkflowId, default))!);
    }

    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-09-06T00:00:00Z"); }
    private sealed class DeferredAgent : IAgentRunner
    {
        public int Started;
        public Dictionary<string, AgentRunResult> Results = [];
        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken token) => Task.FromResult(new AgentRunStartResult($"job-{++Started}"));
        public Task<AgentRunResult?> TryGetResultAsync(string id, CancellationToken token) => Task.FromResult(Results.GetValueOrDefault(id));
    }
}
