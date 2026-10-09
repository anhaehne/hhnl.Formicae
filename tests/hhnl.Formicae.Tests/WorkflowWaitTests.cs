using System.Text.Json;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowWaitTests
{
    private const string Repository = "https://github.com/acme/repo";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-08T20:00:00Z");
    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = Start; }
    private sealed class Prompt : IPromptRenderer
    {
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? item, CancellationToken token) => Task.FromResult("test");
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? item, IReadOnlyList<PullRequestComment> comments, CancellationToken token) => Task.FromResult("test");
    }
    private sealed class Agent : IAgentRunner
    {
        public List<AgentTask> Tasks { get; } = [];
        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken token)
        { Tasks.Add(task); return Task.FromResult(new AgentRunStartResult("fake", new(true, "fake", "done", null))); }
        public Task<AgentRunResult?> TryGetResultAsync(string id, CancellationToken token) => Task.FromResult<AgentRunResult?>(null);
    }
    private static WorkflowDefinitionStep Wait(string id, string? next = null) => new(id, GitHubIssueCommentWaitDefinition.Uses, next, Wait: new(IssueNumber: 7));
    private static WorkflowWaitEvent Event(int id, DateTimeOffset created, string? issue = null, string? delivery = null, DateTimeOffset? received = null) => new()
    {
        Provider = "GitHub", Uses = GitHubIssueCommentWaitDefinition.Uses, DeliveryId = delivery ?? $"delivery-{id}", EventSequence = id, EventKey = id.ToString(),
        RepositoryUrl = Repository, IssueUrl = issue ?? Repository + "/issues/7", CreatedAt = created, ReceivedAt = received ?? created.AddSeconds(1),
        OutputsJson = JsonSerializer.Serialize(new { commentId = id.ToString(), body = $"comment {id}", author = "reviewer", url = Repository + $"/issues/7#issuecomment-{id}", createdAt = created.ToString("O") })
    };
    private static async Task<(InMemoryWorkflowStore Store, Workflow Workflow, InMemoryDevOpsIntegrationStore Integrations, Clock Clock, Agent Agent)> Setup(WorkflowDefinitionDocument document)
    {
        var store = new InMemoryWorkflowStore(); var clock = new Clock(); var agent = new Agent();
        var integrations = new InMemoryDevOpsIntegrationStore();
        var integration = await integrations.CreateAsync(new() { ProviderType = DevOpsProviderType.GitHub }, default);
        await integrations.AddRepositoryAsync(new() { DevOpsIntegrationId = integration.Id, RepositoryUrl = Repository }, default);
        var definition = await store.CreateWorkflowDefinitionAsync(new() { Name = "Wait" }, default);
        var version = await store.CreateWorkflowDefinitionVersionAsync(new() { WorkflowDefinitionId = definition.Id, Version = 1, IsEnabled = true,
            DslSchemaVersion = document.Schema, DefinitionJson = WorkflowDefinitionJson.Serialize(document) }, default);
        var workflow = await store.CreateWorkflowAsync(new() { IssueUrl = Repository + "/issues/1", RepositoryUrl = Repository,
            WorkflowDefinitionId = definition.Id, WorkflowDefinitionVersionId = version.Id, CurrentDefinitionStepId = WorkflowNodeDefinitions.Normalize(document).StartStepId }, default);
        return (store, workflow, integrations, clock, agent);
    }
    private static WorkflowOrchestrator Orchestrator(InMemoryWorkflowStore store, InMemoryDevOpsIntegrationStore integrations, Clock clock, Agent agent)
        => new(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new Prompt(), clock, integrations);

    [Fact]
    public async Task Unbounded_comment_cycle_arms_distinct_waits_and_does_not_reuse_old_comments()
    {
        var (store, workflow, integrations, clock, agent) = await Setup(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "wait", [Wait("wait", "wait")]));
        for (var visit = 1; visit <= 4; visit++)
        {
            await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
            var run = (await store.GetTaskRunExecutionAsync(workflow.Id, "wait", visit, default))!;
            Assert.Equal(TaskRunStatus.Waiting, run.Status);
            await store.AcceptWaitEventAsync(Event(visit, clock.UtcNow.AddSeconds(1)), default);
            clock.UtcNow = clock.UtcNow.AddSeconds(2);
            await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
            Assert.Equal(TaskRunStatus.Succeeded, run.Status);
            Assert.Contains($"comment {visit}", run.StructuredOutputsJson!);
        }
        await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        Assert.Equal(TaskRunStatus.Waiting, (await store.GetTaskRunExecutionAsync(workflow.Id, "wait", 5, default))!.Status);
        Assert.Equal(5, (await store.ListWaitsAsync(workflow.Id, default)).Select(wait => wait.ExecutionAttemptId).Distinct().Count());
        Assert.Empty(agent.Tasks);
    }

    [Fact]
    public async Task Multiple_distinct_comments_claim_one_activation_and_launch_one_continuation_after_restart()
    {
        var (store, workflow, integrations, clock, agent) = await Setup(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "wait", [Wait("wait", "after"), new("after", "builtins.script", Script: new("echo done"))]));
        await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        var run = Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default));
        Assert.Equal(TaskRunStatus.Waiting, run.Status); Assert.Empty(agent.Tasks);
        var wait = Assert.Single(await store.ListWaitsAsync(workflow.Id, default));
        await Task.WhenAll(Enumerable.Range(1, 8).Select(id => store.AcceptWaitEventAsync(Event(id, Start.AddSeconds(id)), default)));
        var claims = await Task.WhenAll(Enumerable.Range(1, 8).Select(_ => store.ClaimWaitEventAsync(wait.Id, default)));
        Assert.Single(claims.Select(item => item!.Id).Distinct());
        for (var i = 0; i < 4; i++) await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        Assert.Single(agent.Tasks); Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal("1", JsonDocument.Parse(run.StructuredOutputsJson!).RootElement.GetProperty("commentId").GetString());
        Assert.Single(await store.ListWaitsAsync(workflow.Id, default));
    }

    [Fact]
    public async Task Loop_reentry_requires_a_new_comment_and_does_not_queue_extra_comments()
    {
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "loop", [
            new("loop", "builtins.loop", "after", Loop: new("wait", 2, 2)),
            Wait("wait", "loop") with { NextStepPort = "return" }, new("after", "builtins.script", Script: new("echo done"))]);
        var (store, workflow, integrations, clock, agent) = await Setup(document);
        await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        await store.AcceptWaitEventAsync(Event(1, Start.AddSeconds(1)), default);
        await store.AcceptWaitEventAsync(Event(2, Start.AddSeconds(2)), default);
        clock.UtcNow = Start.AddSeconds(10);
        await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        var runs = await store.ListTaskRunsAsync(workflow.Id, default);
        Assert.Equal(2, runs.Count); Assert.Equal(TaskRunStatus.Waiting, runs.Single(run => run.LoopIteration == 2).Status);
        Assert.Empty(agent.Tasks);
        Assert.False(await store.AcceptWaitEventAsync(Event(1, Start.AddSeconds(11), delivery: "redelivered"), default));
        await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default); Assert.Empty(agent.Tasks);
        await store.AcceptWaitEventAsync(Event(3, Start.AddSeconds(11)), default);
        for (var i = 0; i < 3; i++) await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        Assert.Single(agent.Tasks); Assert.Equal(WorkflowStatus.Completed, workflow.Status);
    }

    [Fact]
    public async Task Operator_pause_retains_match_but_resume_without_comment_does_not_satisfy_wait()
    {
        var (store, workflow, integrations, clock, agent) = await Setup(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "wait", [Wait("wait")]));
        await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        workflow.IsPaused = true;
        await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        workflow.IsPaused = false;
        await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        Assert.Equal(TaskRunStatus.Waiting, Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default)).Status);
        workflow.IsPaused = true;
        await store.AcceptWaitEventAsync(Event(1, Start.AddSeconds(1)), default);
        await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        Assert.NotNull(Assert.Single(await store.ListWaitsAsync(workflow.Id, default)).MatchedEventId);
        Assert.NotEqual(WorkflowStatus.Completed, workflow.Status);
        workflow.IsPaused = false;
        await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
    }

    [Fact]
    public async Task Cancel_invalidates_wait_without_a_worker_and_late_comments_cannot_revive_it()
    {
        var (store, workflow, integrations, clock, agent) = await Setup(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "wait", [Wait("wait")]));
        var orchestrator = Orchestrator(store, integrations, clock, agent);
        await orchestrator.AdvanceAsync(workflow, default);
        workflow.CancelRequestedAt = clock.UtcNow;
        await store.AcceptWaitEventAsync(Event(1, Start.AddSeconds(1)), default);
        await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Canceled, workflow.Status);
        Assert.Equal(TaskRunStatus.Canceled, Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default)).Status);
        var wait = Assert.Single(await store.ListWaitsAsync(workflow.Id, default));
        Assert.True(wait.IsCanceled); Assert.Null(await store.ClaimWaitEventAsync(wait.Id, default)); Assert.Empty(agent.Tasks);
    }

    [Fact]
    public async Task Wrong_issue_and_late_old_comment_do_not_satisfy_wait()
    {
        var (store, workflow, integrations, clock, agent) = await Setup(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "wait", [Wait("wait")]));
        await store.AcceptWaitEventAsync(Event(1, Start.AddSeconds(-1), received: Start.AddSeconds(1)), default);
        await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        await store.AcceptWaitEventAsync(Event(2, Start.AddSeconds(1), Repository + "/issues/8"), default);
        await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        Assert.Equal(TaskRunStatus.Waiting, Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default)).Status);
    }

    [Fact]
    public async Task Ordinary_branch_runs_while_waiting_and_join_waits_for_comment()
    {
        var (store, workflow, integrations, clock, agent) = await Setup(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "entry", [
            new("entry", "builtins.script", "wait", Script: new("echo entry"), NextStepIds: ["sibling"]), Wait("wait", "join"),
            new("sibling", "builtins.script", "join", Script: new("echo sibling")), new("join", "builtins.script", Script: new("echo join"))]));
        for (var i = 0; i < 4; i++) await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        Assert.Equal(2, agent.Tasks.Count); Assert.NotEqual(WorkflowStatus.Completed, workflow.Status);
        await store.AcceptWaitEventAsync(Event(1, Start.AddSeconds(1)), default);
        for (var i = 0; i < 3; i++) await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        Assert.Equal(3, agent.Tasks.Count); Assert.Equal(WorkflowStatus.Completed, workflow.Status);
    }
    [Fact]
    public async Task Issue_number_binding_is_frozen_and_comment_body_binds_to_downstream_agent()
    {
        var raw = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "producer", [
            new("producer", "builtins.agent-task", "wait", CustomTask: new("", Definition: new("Choose issue", [], new(), [new("number", "number", true)]))),
            new("wait", GitHubIssueCommentWaitDefinition.Uses, "after", Wait: new(IssueNumberBinding: new("producer", "number"))),
            new("after", "builtins.agent-task", CustomTask: new("", Bindings: new Dictionary<string, CustomTaskInputBinding> { ["feedback"] = new("wait", "body") },
                Definition: new("Use {{input.feedback}}", [new("feedback", "string", true)], new())))
        ]);
        var resolved = await CustomTaskDefinitions.ResolveAsync(raw, null, default);
        Assert.True(resolved.Validation.IsValid, string.Join(";", resolved.Validation.Errors.Select(error => error.Message)));
        var (store, workflow, integrations, clock, agent) = await Setup(resolved.Document);
        var producer = await store.UpsertTaskRunAsync(new() { WorkflowId = workflow.Id, DefinitionStepId = "producer", Kind = TaskRunKind.Custom,
            Status = TaskRunStatus.Succeeded, ExecutionAttemptId = Guid.NewGuid(), StructuredOutputsJson = "{\"number\":7}" }, default);
        workflow.CurrentDefinitionStepId = "wait";
        await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        var wait = Assert.Single(await store.ListWaitsAsync(workflow.Id, default));
        Assert.EndsWith("/issues/7", wait.IssueUrl); Assert.Contains(producer.Id.ToString(), wait.InputProvenanceJson);
        await store.AcceptWaitEventAsync(Event(1, Start.AddSeconds(1)), default);
        await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        Assert.Contains("comment 1", Assert.Single(agent.Tasks).Prompt);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
    }

    [Fact]
    public void Issue_number_bindings_reject_downstream_and_incompatible_producers()
    {
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "wait", [
            new("wait", GitHubIssueCommentWaitDefinition.Uses, "producer", Wait: new(IssueNumberBinding: new("producer", "output"))),
            new("producer", "builtins.script", Script: new("echo 7"))]);
        Assert.NotEmpty(CustomTaskDefinitions.ValidateBindings(document));
        Assert.False(new WorkflowDefinitionValidator().Validate(document with { Steps = [Wait("wait") with { Wait = new(IssueNumber: 0) }] }).IsValid);
    }

    [Fact]
    public async Task New_comment_in_activation_second_is_accepted_without_replaying_existing_comments()
    {
        var (store, workflow, integrations, clock, agent) = await Setup(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "wait", [Wait("wait")]));
        clock.UtcNow = Start.AddMilliseconds(400);
        await store.AcceptWaitEventAsync(Event(10, Start, received: Start.AddMilliseconds(100)), default);
        await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        await store.AcceptWaitEventAsync(Event(11, Start, received: Start.AddMilliseconds(900)), default);
        await Orchestrator(store, integrations, clock, agent).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal("11", JsonDocument.Parse(Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default)).StructuredOutputsJson!).RootElement.GetProperty("commentId").GetString());
    }

}
