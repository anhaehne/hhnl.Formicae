using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Tests.TestDoubles;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowServiceTests
{
    [Fact]
    public async Task ListRecentWorkflowsAsync_returns_newest_first()
    {
        var store = new InMemoryWorkflowStore();
        var service = new WorkflowService(store);
        var older = await CreateWorkflowAsync(store, "https://github.com/acme/widgets/issues/1", new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero));
        var newer = await CreateWorkflowAsync(store, "https://github.com/acme/widgets/issues/2", new DateTimeOffset(2026, 6, 25, 12, 0, 0, TimeSpan.Zero));
        var newest = await CreateWorkflowAsync(store, "https://github.com/acme/widgets/issues/3", new DateTimeOffset(2026, 6, 26, 12, 0, 0, TimeSpan.Zero));

        var workflows = await service.ListRecentWorkflowsAsync(10, CancellationToken.None);

        Assert.Collection(workflows,
            workflow => Assert.Equal(newest.Id, workflow.WorkflowId),
            workflow => Assert.Equal(newer.Id, workflow.WorkflowId),
            workflow => Assert.Equal(older.Id, workflow.WorkflowId));
    }

    [Fact]
    public async Task ListRecentWorkflowsAsync_respects_limit()
    {
        var store = new InMemoryWorkflowStore();
        var service = new WorkflowService(store);
        await CreateWorkflowAsync(store, "https://github.com/acme/widgets/issues/1", new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero));
        var newer = await CreateWorkflowAsync(store, "https://github.com/acme/widgets/issues/2", new DateTimeOffset(2026, 6, 25, 12, 0, 0, TimeSpan.Zero));
        var newest = await CreateWorkflowAsync(store, "https://github.com/acme/widgets/issues/3", new DateTimeOffset(2026, 6, 26, 12, 0, 0, TimeSpan.Zero));

        var workflows = await service.ListRecentWorkflowsAsync(2, CancellationToken.None);

        Assert.Collection(workflows,
            workflow => Assert.Equal(newest.Id, workflow.WorkflowId),
            workflow => Assert.Equal(newer.Id, workflow.WorkflowId));
    }

    [Fact]
    public async Task StartGitHubIssueWorkflowAsync_records_workflow_queued_event()
    {
        var store = new InMemoryWorkflowStore();
        var clock = new FixedClock(DateTimeOffset.Parse("2026-06-26T12:00:00Z"));
        var service = new WorkflowService(store, clock: clock);

        var workflow = await service.StartGitHubIssueWorkflowAsync(new StartGitHubIssueWorkflowRequest(
            "https://github.com/acme/widgets/issues/4",
            "https://github.com/acme/widgets",
            null,
            null), CancellationToken.None);

        var events = await service.ListEventsAsync(workflow.WorkflowId, CancellationToken.None);
        var queued = Assert.Single(events);
        Assert.Equal(WorkflowEventTypes.WorkflowQueued, queued.Type);
        Assert.Equal(clock.UtcNow, queued.CreatedAt);
    }

    [Fact]
    public async Task ListChatMessagesAsync_excludes_automation_comments()
    {
        var store = new InMemoryWorkflowStore();
        var issueUrl = "https://github.com/acme/widgets/issues/5";
        var devOps = new MockDevOpsAdapter()
            .AddIssueComment(issueUrl, "human", "alice", "Please add more tests.", DateTimeOffset.Parse("2026-06-26T10:00:00Z"))
            .AddIssueComment(issueUrl, "automation", "formicae", PullRequestCommentMarkers.Plan(Guid.NewGuid()) + " automated plan", DateTimeOffset.Parse("2026-06-26T11:00:00Z"));
        var workflow = await CreateWorkflowAsync(store, issueUrl, DateTimeOffset.Parse("2026-06-26T09:00:00Z"));
        var service = new WorkflowService(store, devOps);

        var messages = await service.ListChatMessagesAsync(workflow.Id, CancellationToken.None);

        var message = Assert.Single(messages);
        Assert.Equal("human", message.Id);
        Assert.Equal("alice", message.Author);
        Assert.Equal("Please add more tests.", message.Body);
    }

    [Fact]
    public void AgentMessageParser_parses_json_lines_and_rejects_malformed_output()
    {
        var parsed = AgentMessageParser.Parse("""
            {"role":"assistant","content":"Planning work","timestamp":"2026-06-26T12:00:00Z"}
            {"source":"tool","message":"dotnet test passed"}
            """);

        Assert.Collection(parsed,
            message =>
            {
                Assert.Equal(0, message.Sequence);
                Assert.Equal("assistant", message.Role);
                Assert.Equal("Planning work", message.Content);
                Assert.Equal(DateTimeOffset.Parse("2026-06-26T12:00:00Z"), message.CreatedAt);
            },
            message =>
            {
                Assert.Equal(1, message.Sequence);
                Assert.Equal("tool", message.Role);
                Assert.Equal("dotnet test passed", message.Content);
            });
        Assert.Empty(AgentMessageParser.Parse("plain text output"));
    }

    [Fact]
    public async Task WorkflowObservabilityService_reports_stale_and_stuck_signals()
    {
        var store = new InMemoryWorkflowStore();
        var now = DateTimeOffset.Parse("2026-06-26T12:00:00Z");
        var workflow = await store.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = "https://github.com/acme/widgets/issues/6",
            RepositoryUrl = "https://github.com/acme/widgets",
            Status = WorkflowStatus.Planning,
            CurrentStep = WorkflowStep.Plan,
            UpdatedAt = now.AddHours(-3)
        }, CancellationToken.None);
        var run = await store.UpsertTaskRunAsync(new TaskRun
        {
            WorkflowId = workflow.Id,
            Kind = TaskRunKind.Plan,
            Status = TaskRunStatus.Running,
            StartedAt = now.AddMinutes(-45),
            UpdatedAt = now.AddMinutes(-45)
        }, CancellationToken.None);
        var service = new WorkflowObservabilityService(
            store,
            new FixedClock(now),
            Options.Create(new WorkflowObservabilityOptions
            {
                RunningTaskStuckAfter = TimeSpan.FromMinutes(30),
                WorkflowStaleAfter = TimeSpan.FromHours(2)
            }));

        var signals = await service.GetWorkflowSignalsAsync(workflow.Id, CancellationToken.None);

        Assert.Contains(signals, signal => signal.TaskRunId == run.Id && signal.Reason.Contains("longer than", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(signals, signal => signal.TaskRunId == run.Id && signal.Reason.Contains("without an external job id", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(signals, signal => signal.TaskRunId is null && signal.Reason.Contains("not been updated", StringComparison.OrdinalIgnoreCase));
        Assert.All(signals, signal => Assert.Equal(now, signal.ObservedAt));
    }

    private static Task<Workflow> CreateWorkflowAsync(InMemoryWorkflowStore store, string issueUrl, DateTimeOffset createdAt)
        => store.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = issueUrl,
            RepositoryUrl = "https://github.com/acme/widgets",
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        }, CancellationToken.None);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
