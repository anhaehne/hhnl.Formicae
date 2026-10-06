using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowExecutionPersistenceTests(MigrationPostgresFixture fixture) : IClassFixture<MigrationPostgresFixture>
{
    [Fact]
    public async Task Migration_backfills_unique_cursors_for_existing_log_history()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await db.GetService<IMigrator>().MigrateAsync("20261001205950_AddTaskDataPassing");
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO workflow_logs (\"Id\", \"WorkflowId\", \"Level\", \"Message\", \"CreatedAt\") VALUES ({Guid.NewGuid()}, {id}, 'Information', 'old one', {DateTimeOffset.UtcNow}), ({Guid.NewGuid()}, {id}, 'Information', 'old two', {DateTimeOffset.UtcNow})");
        await db.Database.MigrateAsync();
        var logs = await db.WorkflowLogs.OrderBy(log => log.Sequence).ToArrayAsync();
        Assert.Equal(2, logs.Length); Assert.All(logs, log => Assert.True(log.Sequence > 0));
        Assert.NotEqual(logs[0].Sequence, logs[1].Sequence); Assert.All(logs, log => Assert.Equal("system", log.Source));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Concurrent_append_cannot_publish_cursor_past_an_uncommitted_log()
    {
        await using var first = await fixture.CreateDatabaseAsync();
        await first.Database.MigrateAsync();
        var store = new EfWorkflowStore(first);
        var workflow = await store.CreateWorkflowAsync(new Workflow { IssueUrl = "https://example.test/1", RepositoryUrl = "https://example.test/repo" }, default);
        var options = new DbContextOptionsBuilder<FormicaeDbContext>().UseNpgsql(first.Database.GetConnectionString()!).Options;
        await using var second = new FormicaeDbContext(options);
        await using var observer = new FormicaeDbContext(options);
        await using var transaction = await first.Database.BeginTransactionAsync();
        await first.Workflows.FromSqlInterpolated($"SELECT * FROM workflows WHERE \"Id\" = {workflow.Id} FOR UPDATE").AsNoTracking().SingleAsync();
        var early = new WorkflowLog { WorkflowId = workflow.Id, Message = "early" };
        first.WorkflowLogs.Add(early); await first.SaveChangesAsync();
        var later = new WorkflowLog { WorkflowId = workflow.Id, Message = "later" };
        var append = new EfWorkflowStore(second).AppendLogsAsync([later], default);
        // An uncommitted early log holds the same workflow lock used before any later identity allocation.
        Assert.Empty((await new EfWorkflowStore(observer).QueryLogsAsync(workflow.Id, new(After: 0), default)).Items);
        Assert.False(append.IsCompleted);
        await transaction.CommitAsync(); await append;
        Assert.True(later.Sequence > early.Sequence);
        var replay = await new EfWorkflowStore(observer).QueryLogsAsync(workflow.Id, new(After: early.Sequence), default);
        Assert.Equal(later.Id, Assert.Single(replay.Items).Id);
    }

    [Fact]
    public async Task Sql_query_and_worker_dedup_preserve_attempt_identity()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await db.Database.MigrateAsync();
        var store = new EfWorkflowStore(db);
        var workflow = await store.CreateWorkflowAsync(new Workflow { IssueUrl = "https://example.test/1", RepositoryUrl = "https://example.test/repo" }, default);
        var run = new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.Plan, Status = TaskRunStatus.Running,
            ExecutionAttemptId = Guid.NewGuid(), ExternalId = "job" };
        await store.UpsertTaskRunAsync(run, default);
        var log = new WorkflowLog { WorkflowId = workflow.Id, TaskRunId = run.Id, ExecutionAttemptId = run.ExecutionAttemptId,
            Source = "stdout", SourceSequence = 1, Message = "searchable" };
        Assert.True(await store.TryAddWorkerLogAsync(log, "job", run.ExecutionAttemptId, default));
        Assert.True(await store.TryAddWorkerLogAsync(log, "job", run.ExecutionAttemptId, default));
        Assert.Single((await store.QueryLogsAsync(workflow.Id, new(Search: "SEARCH", Source: "stdout", ExecutionAttemptId: run.ExecutionAttemptId), default)).Items);
        Assert.Single((await store.SearchWorkflowsAsync(new(RepositoryUrl: workflow.RepositoryUrl), default)).Items);
        run.Status = TaskRunStatus.Failed; await store.UpsertTaskRunAsync(run, default);
        await store.ArchiveTaskRunAttemptAsync(run, default); await store.ArchiveTaskRunAttemptAsync(run, default);
        Assert.Single(await store.ListTaskRunAttemptsAsync(workflow.Id, default));
        Assert.False(await store.TryAddWorkerLogAsync(new WorkflowLog { WorkflowId = workflow.Id, TaskRunId = run.Id,
            ExecutionAttemptId = run.ExecutionAttemptId, Message = "late" }, "job", run.ExecutionAttemptId, default));
    }
}
