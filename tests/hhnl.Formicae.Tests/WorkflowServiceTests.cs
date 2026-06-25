using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

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

    private static Task<Workflow> CreateWorkflowAsync(InMemoryWorkflowStore store, string issueUrl, DateTimeOffset createdAt)
        => store.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = issueUrl,
            RepositoryUrl = "https://github.com/acme/widgets",
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        }, CancellationToken.None);
}
