using hhnl.Formicae.Tests.TestDoubles;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowConcurrencyTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-10T20:00:00Z");
    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = Start; }
    private sealed class Agent : IAgentRunner
    {
        public List<AgentTask> Tasks { get; } = [];
        public HashSet<string> Finished { get; } = [];
        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken token)
        { Tasks.Add(task); return Task.FromResult(new AgentRunStartResult(task.WorkflowId.ToString(), null)); }
        public Task CancelAsync(string id, CancellationToken token) => Task.CompletedTask;
        public Task<AgentRunResult?> TryGetResultAsync(string id, CancellationToken token)
            => Task.FromResult<AgentRunResult?>(Finished.Contains(id) ? new(true, id, "done", null) : null);
    }
    private sealed class Prompt : IPromptRenderer
    {
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? item, CancellationToken token) => Task.FromResult("test");
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? item, IReadOnlyList<PullRequestComment> comments, CancellationToken token) => Task.FromResult("test");
    }
    private sealed class Setup
    {
        public InMemoryWorkflowStore Store { get; } = new();
        public Clock Clock { get; } = new();
        public Agent Agent { get; } = new();
        public InMemoryDevOpsIntegrationStore Integrations { get; } = new();
        public WorkflowOrchestrator Orchestrator => new(Store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), Agent, new Prompt(), Clock, Integrations);
        public async Task<Workflow> Add(Guid? definitionId = null, int? limit = null, WorkflowDefinitionDocument? document = null)
        {
            var definition = definitionId is { } id ? (await Store.GetWorkflowDefinitionAsync(id, default))!
                : await Store.CreateWorkflowDefinitionAsync(new() { Name = "Limited", MaxActiveInstances = limit }, default);
            document ??= new(DefaultWorkflowDefinitions.V1Alpha3Schema, "script", [new("script", "builtins.script", Script: new("echo test"))]);
            var version = await Store.CreateWorkflowDefinitionVersionAsync(new() { WorkflowDefinitionId = definition.Id,
                Version = (await Store.ListWorkflowDefinitionVersionsAsync(definition.Id, default)).Count + 1, IsEnabled = true,
                DslSchemaVersion = document.Schema, DefinitionJson = WorkflowDefinitionJson.Serialize(document) }, default);
            var workflow = await Store.CreateWorkflowAsync(new() { IssueUrl = $"https://github.com/acme/repo/issues/{Guid.NewGuid()}",
                RepositoryUrl = "https://github.com/acme/repo", WorkflowDefinitionId = definition.Id, WorkflowDefinitionVersionId = version.Id,
                CurrentDefinitionStepId = document.StartStepId, CreatedAt = Clock.UtcNow, EnqueuedAt = Clock.UtcNow }, default);
            Clock.UtcNow = Clock.UtcNow.AddSeconds(1);
            return workflow;
        }
    }

    [Fact]
    public async Task Global_limit_admits_fifo_and_retains_queue_across_orchestrator_restart()
    {
        var setup = new Setup(); await setup.Store.SetConcurrencySettingsAsync(new() { MaxActiveInstances = 1 }, default);
        var first = await setup.Add(); var second = await setup.Add(); var third = await setup.Add();
        Assert.False(await setup.Orchestrator.AdvanceAsync(third, default)); Assert.Empty(setup.Agent.Tasks);
        await setup.Orchestrator.AdvanceAsync(first, default);
        Assert.Single(setup.Agent.Tasks); Assert.True(first.HasExecutionSlot); Assert.NotNull(second.EnqueuedAt);
        setup.Agent.Finished.Add(first.Id.ToString()); await setup.Orchestrator.AdvanceAsync(first, default);
        await setup.Orchestrator.AdvanceAsync(third, default); Assert.Single(setup.Agent.Tasks);
        await setup.Orchestrator.AdvanceAsync(second, default);
        Assert.Equal(new[] { first.Id, second.Id }, setup.Agent.Tasks.Select(task => task.WorkflowId));
        Assert.False(first.HasExecutionSlot); Assert.NotNull(third.EnqueuedAt);
    }

    [Fact]
    public async Task Definition_limit_is_shared_across_versions_and_skips_blocked_definition()
    {
        var setup = new Setup(); await setup.Store.SetConcurrencySettingsAsync(new() { MaxActiveInstances = 2 }, default);
        var first = await setup.Add(limit: 1); var second = await setup.Add(first.WorkflowDefinitionId); var other = await setup.Add();
        await setup.Orchestrator.AdvanceRunnableWorkflowsAsync(default);
        Assert.Equal(new[] { first.Id, other.Id }, setup.Agent.Tasks.Select(task => task.WorkflowId));
        Assert.NotNull(second.EnqueuedAt); Assert.False(second.HasExecutionSlot);
        Assert.NotEqual(first.WorkflowDefinitionVersionId, second.WorkflowDefinitionVersionId);
    }

    [Fact]
    public async Task Lowering_limits_drains_existing_workers_and_does_not_admit_more()
    {
        var setup = new Setup(); var orchestrator = setup.Orchestrator; var first = await setup.Add(); var second = await setup.Add();
        await orchestrator.AdvanceRunnableWorkflowsAsync(default); Assert.Equal(2, setup.Agent.Tasks.Count);
        await setup.Store.SetConcurrencySettingsAsync(new() { MaxActiveInstances = 1 }, default); var third = await setup.Add();
        await orchestrator.AdvanceRunnableWorkflowsAsync(default); Assert.Equal(2, setup.Agent.Tasks.Count);
        Assert.True(first.HasExecutionSlot); Assert.True(second.HasExecutionSlot);
        setup.Agent.Finished.Add(first.Id.ToString()); await orchestrator.AdvanceRunnableWorkflowsAsync(default);
        await orchestrator.AdvanceRunnableWorkflowsAsync(default); Assert.Equal(2, setup.Agent.Tasks.Count);
        setup.Agent.Finished.Add(second.Id.ToString()); await orchestrator.AdvanceRunnableWorkflowsAsync(default);
        await orchestrator.AdvanceRunnableWorkflowsAsync(default); Assert.Equal(3, setup.Agent.Tasks.Count);
    }

    [Fact]
    public async Task Paused_worker_counts_until_finished_and_resume_rejoins_queue()
    {
        var setup = new Setup(); await setup.Store.SetConcurrencySettingsAsync(new() { MaxActiveInstances = 1 }, default);
        var first = await setup.Add(document: new(DefaultWorkflowDefinitions.V1Alpha3Schema, "script", [
            new("script", "builtins.script", "after", Script: new("echo test")), new("after", "builtins.script", Script: new("echo next"))]));
        var second = await setup.Add(); await setup.Orchestrator.AdvanceAsync(first, default);
        var controls = new WorkflowExecutionService(setup.Store, setup.Clock);
        await controls.SetControlAsync(first.Id, "pause", default);
        await setup.Orchestrator.AdvanceAsync(second, default); Assert.Single(setup.Agent.Tasks);
        setup.Agent.Finished.Add(first.Id.ToString()); await setup.Orchestrator.AdvanceAsync(first, default);
        await controls.SetControlAsync(first.Id, "resume", default);
        await setup.Orchestrator.AdvanceAsync(first, default); Assert.Single(setup.Agent.Tasks);
        await setup.Orchestrator.AdvanceAsync(second, default); Assert.Equal(2, setup.Agent.Tasks.Count);
    }

    [Fact]
    public async Task Parallel_graph_workers_count_as_one_instance_and_cancellation_releases_it()
    {
        var setup = new Setup(); await setup.Store.SetConcurrencySettingsAsync(new() { MaxActiveInstances = 1 }, default);
        var graph = await setup.Add(document: new(DefaultWorkflowDefinitions.V1Alpha3Schema, "root", [
            new("root", "builtins.script", "left", Script: new("echo root"), NextStepIds: ["right"]),
            new("left", "builtins.script", Script: new("echo left")), new("right", "builtins.script", Script: new("echo right"))]));
        var next = await setup.Add(); await setup.Orchestrator.AdvanceAsync(graph, default);
        setup.Agent.Finished.Add(graph.Id.ToString()); await setup.Orchestrator.AdvanceAsync(graph, default);
        setup.Agent.Finished.Clear(); await setup.Orchestrator.AdvanceAsync(graph, default);
        Assert.Equal(3, setup.Agent.Tasks.Count); Assert.True(graph.HasExecutionSlot);
        await setup.Orchestrator.AdvanceAsync(next, default); Assert.Equal(3, setup.Agent.Tasks.Count);
        await new WorkflowExecutionService(setup.Store, setup.Clock).SetControlAsync(graph.Id, "cancel", default);
        await setup.Orchestrator.AdvanceAsync(graph, default); Assert.False(graph.HasExecutionSlot);
        await setup.Orchestrator.AdvanceAsync(next, default); Assert.Equal(4, setup.Agent.Tasks.Count);
    }

    [Fact]
    public async Task Planning_approval_wait_does_not_hold_capacity()
    {
        var setup = new Setup(); await setup.Store.SetConcurrencySettingsAsync(new() { MaxActiveInstances = 1 }, default);
        var waiting = await setup.Add(document: new(DefaultWorkflowDefinitions.V1Alpha3Schema, "plan", [new("plan", "builtins.plan")]));
        var provider = new MockDevOpsAdapter().AddIssueWithLabels(waiting.IssueUrl, "Waiting", "Body", []);
        var orchestrator = new WorkflowOrchestrator(setup.Store, provider, new FakeSourceControlProvider(), setup.Agent, new Prompt(), setup.Clock);
        await orchestrator.AdvanceAsync(waiting, default); Assert.False(waiting.HasExecutionSlot); Assert.Null(waiting.EnqueuedAt);
        var next = await setup.Add(); await orchestrator.AdvanceAsync(next, default); Assert.Single(setup.Agent.Tasks);
    }

    [Fact]
    public async Task Provider_unavailability_without_workers_releases_capacity_for_other_definitions()
    {
        var setup = new Setup(); await setup.Store.SetConcurrencySettingsAsync(new() { MaxActiveInstances = 1 }, default);
        var unavailable = await setup.Add(document: new(DefaultWorkflowDefinitions.V1Alpha3Schema, "plan", [new("plan", "builtins.plan")]));
        unavailable.HasExecutionSlot = true; unavailable.EnqueuedAt = null;
        var other = await setup.Add();
        var provider = new MockDevOpsAdapter { GetIssueException = new WorkItemProviderUnavailableException("Temporary outage.") };
        var orchestrator = new WorkflowOrchestrator(setup.Store, provider, new FakeSourceControlProvider(), setup.Agent, new Prompt(), setup.Clock);
        await orchestrator.AdvanceRunnableWorkflowsAsync(default);
        Assert.False(unavailable.HasExecutionSlot); Assert.Null(unavailable.EnqueuedAt);
        Assert.Equal(other.Id, Assert.Single(setup.Agent.Tasks).WorkflowId);
    }

    [Fact]
    public async Task First_approval_reactivation_joins_at_tail_instead_of_using_original_creation_time()
    {
        var setup = new Setup(); await setup.Store.SetConcurrencySettingsAsync(new() { MaxActiveInstances = 1 }, default);
        var waiting = await setup.Add(document: new(DefaultWorkflowDefinitions.V1Alpha3Schema, "plan", [new("plan", "builtins.plan")]));
        var provider = new MockDevOpsAdapter().AddIssueWithLabels(waiting.IssueUrl, "Waiting", "Body", []);
        var orchestrator = new WorkflowOrchestrator(setup.Store, provider, new FakeSourceControlProvider(), setup.Agent, new Prompt(), setup.Clock);
        await orchestrator.AdvanceAsync(waiting, default); Assert.Null(waiting.EnqueuedAt);
        var active = await setup.Add(); await orchestrator.AdvanceAsync(active, default); var older = await setup.Add();
        provider.AddIssueWithLabels(waiting.IssueUrl, "Ready", "Body", [WorkItemWorkflowLabels.ReadyToPlan]);
        await orchestrator.AdvanceAsync(waiting, default);
        Assert.True(waiting.EnqueuedAt > older.EnqueuedAt); Assert.False(waiting.HasExecutionSlot);
        setup.Agent.Finished.Add(active.Id.ToString()); await orchestrator.AdvanceAsync(active, default);
        await orchestrator.AdvanceAsync(waiting, default); Assert.Single(setup.Agent.Tasks);
        await orchestrator.AdvanceAsync(older, default); Assert.Equal(2, setup.Agent.Tasks.Count);
    }

    [Fact]
    public async Task Waiting_instance_releases_capacity_and_matched_event_waits_behind_older_queue()
    {
        var setup = new Setup();
        var integration = await setup.Integrations.CreateAsync(new() { ProviderType = DevOpsProviderType.GitHub }, default);
        await setup.Integrations.AddRepositoryAsync(new() { DevOpsIntegrationId = integration.Id, RepositoryUrl = "https://github.com/acme/repo" }, default);
        await setup.Store.SetConcurrencySettingsAsync(new() { MaxActiveInstances = 1 }, default);
        var waiting = await setup.Add(document: new(DefaultWorkflowDefinitions.V1Alpha3Schema, "wait", [
            new("wait", GitHubIssueCommentWaitDefinition.Uses, "script", Wait: new(IssueNumber: 7)),
            new("script", "builtins.script", Script: new("echo test"))]));
        await setup.Orchestrator.AdvanceAsync(waiting, default); Assert.False(waiting.HasExecutionSlot); Assert.Null(waiting.EnqueuedAt);
        var active = await setup.Add(); var older = await setup.Add(); await setup.Orchestrator.AdvanceAsync(active, default);
        var wait = Assert.Single(await setup.Store.ListWaitsAsync(waiting.Id, default));
        await setup.Store.AcceptWaitEventAsync(new() { Provider = "GitHub", Uses = GitHubIssueCommentWaitDefinition.Uses,
            RepositoryUrl = "https://github.com/acme/repo", IssueUrl = wait.IssueUrl, DeliveryId = "one", EventKey = "one", EventSequence = 1,
            CreatedAt = setup.Clock.UtcNow, ReceivedAt = setup.Clock.UtcNow, OutputsJson = "{}" }, default);
        await setup.Orchestrator.AdvanceAsync(waiting, default);
        Assert.NotNull(wait.MatchedEventId); Assert.NotNull(waiting.EnqueuedAt);
        Assert.Equal(TaskRunStatus.Waiting, Assert.Single(await setup.Store.ListTaskRunsAsync(waiting.Id, default)).Status);
        setup.Agent.Finished.Add(active.Id.ToString()); await setup.Orchestrator.AdvanceAsync(active, default);
        await setup.Orchestrator.AdvanceAsync(waiting, default); Assert.Single(setup.Agent.Tasks);
        await setup.Orchestrator.AdvanceAsync(older, default); Assert.Equal(2, setup.Agent.Tasks.Count);
    }
}
