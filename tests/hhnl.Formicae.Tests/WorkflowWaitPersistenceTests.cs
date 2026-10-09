using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowWaitPersistenceTests(MigrationPostgresFixture fixture) : IClassFixture<MigrationPostgresFixture>
{
    [Fact]
    public async Task Concurrent_claims_across_contexts_keep_one_winner_and_recover_after_restart()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await db.Database.MigrateAsync();
        var store = new EfWorkflowStore(db);
        var workflow = await store.CreateWorkflowAsync(new() { IssueUrl = "https://github.com/acme/repo/issues/1", RepositoryUrl = "https://github.com/acme/repo", Status = WorkflowStatus.Running }, default);
        var run = await store.UpsertTaskRunAsync(new() { WorkflowId = workflow.Id, Kind = TaskRunKind.Wait, Status = TaskRunStatus.Waiting,
            DefinitionStepId = "wait", ExecutionAttemptId = Guid.NewGuid() }, default);
        var wait = await store.ArmWaitAsync(new() { WorkflowId = workflow.Id, TaskRunId = run.Id, ExecutionAttemptId = run.ExecutionAttemptId!.Value,
            Uses = GitHubIssueCommentWaitDefinition.Uses, Provider = "GitHub", RepositoryUrl = workflow.RepositoryUrl, IssueUrl = workflow.IssueUrl, ArmedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, default);
        for (var i = 0; i < 3; i++) await store.AcceptWaitEventAsync(new() { Provider = "GitHub", DeliveryId = "delivery-" + i,
            EventSequence = i + 1, EventKey = "comment-" + i, Uses = wait.Uses, RepositoryUrl = wait.RepositoryUrl, IssueUrl = wait.IssueUrl,
            CreatedAt = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow.AddMilliseconds(i), OutputsJson = "{}" }, default);
        var options = new DbContextOptionsBuilder<FormicaeDbContext>().UseNpgsql(db.Database.GetConnectionString()!).Options;
        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var context = new FormicaeDbContext(options);
            return await new EfWorkflowStore(context).ClaimWaitEventAsync(wait.Id, default);
        }));
        Assert.Single(claims.Select(evt => evt!.Id).Distinct());
        await using var restarted = new FormicaeDbContext(options);
        var restored = (await new EfWorkflowStore(restarted).GetWaitAsync(run.ExecutionAttemptId.Value, default))!;
        Assert.Equal(claims[0]!.Id, restored.MatchedEventId);
        Assert.Equal(restored.MatchedEventId, (await new EfWorkflowStore(restarted).ClaimWaitEventAsync(wait.Id, default))!.Id);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Duplicate_comment_identity_and_cancellation_are_durable()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await db.Database.MigrateAsync();
        var store = new EfWorkflowStore(db);
        var workflow = await store.CreateWorkflowAsync(new() { IssueUrl = "https://github.com/acme/repo/issues/1", RepositoryUrl = "https://github.com/acme/repo", Status = WorkflowStatus.Running }, default);
        var run = await store.UpsertTaskRunAsync(new() { WorkflowId = workflow.Id, Kind = TaskRunKind.Wait, Status = TaskRunStatus.Waiting,
            DefinitionStepId = "wait", ExecutionAttemptId = Guid.NewGuid() }, default);
        var wait = await store.ArmWaitAsync(new() { WorkflowId = workflow.Id, TaskRunId = run.Id, ExecutionAttemptId = run.ExecutionAttemptId!.Value,
            Uses = GitHubIssueCommentWaitDefinition.Uses, Provider = "GitHub", RepositoryUrl = workflow.RepositoryUrl, IssueUrl = workflow.IssueUrl, ArmedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, default);
        WorkflowWaitEvent Event(string delivery) => new() { Provider = "GitHub", DeliveryId = delivery, EventSequence = 1, EventKey = "same-comment",
            Uses = wait.Uses, RepositoryUrl = wait.RepositoryUrl, IssueUrl = wait.IssueUrl, CreatedAt = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow, OutputsJson = "{}" };
        Assert.True(await store.AcceptWaitEventAsync(Event("first"), default));
        Assert.False(await store.AcceptWaitEventAsync(Event("second"), default));
        workflow.CancelRequestedAt = DateTimeOffset.UtcNow; await store.UpdateWorkflowAsync(workflow, default);
        Assert.Null(await store.ClaimWaitEventAsync(wait.Id, default));
        await store.CancelWaitsAsync(workflow.Id, default);
        db.ChangeTracker.Clear();
        Assert.True((await store.GetWaitAsync(run.ExecutionAttemptId.Value, default))!.IsCanceled);
        Assert.Null(await store.ClaimWaitEventAsync(wait.Id, default));
    }
}
