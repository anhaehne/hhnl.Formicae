using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowParallelPersistenceTests(MigrationPostgresFixture fixture) : IClassFixture<MigrationPostgresFixture>
{
    [Fact]
    public async Task Activation_snapshot_and_attempt_identity_survive_restart_and_update()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await db.Database.MigrateAsync();
        var store = new EfWorkflowStore(db);
        var workflow = await store.CreateWorkflowAsync(new Workflow { IssueUrl = "https://example.test/issues/1", RepositoryUrl = "https://example.test/repo" }, default);
        var execution = await store.UpsertParallelExecutionAsync(new WorkflowParallelExecution { WorkflowId = workflow.Id, NodeId = "parallel", EntryPlanArtifact = "immutable entry" }, default);
        var attemptId = Guid.NewGuid();
        var run = await store.UpsertTaskRunAsync(new TaskRun { WorkflowId = workflow.Id, DefinitionStepId = "branch-a", Kind = TaskRunKind.Plan, ExecutionAttemptId = attemptId }, default);
        workflow.PlanArtifact = "later workflow artifact";
        await store.UpdateWorkflowAsync(workflow, default);
        db.ChangeTracker.Clear();
        var restored = await store.GetParallelExecutionAsync(workflow.Id, "parallel", default);
        Assert.NotNull(restored);
        Assert.Equal(execution.Id, restored.Id);
        Assert.Equal("immutable entry", restored.EntryPlanArtifact);
        Assert.Equal(WorkflowParallelExecutionOutcome.Running, restored.Outcome);
        Assert.Equal(attemptId, (await store.GetTaskRunExecutionAsync(workflow.Id, "branch-a", null, default))!.ExecutionAttemptId);
        restored.Outcome = WorkflowParallelExecutionOutcome.Succeeded;
        restored.CompletedAt = DateTimeOffset.UtcNow;
        await store.UpsertParallelExecutionAsync(restored, default);
        db.ChangeTracker.Clear();
        Assert.Equal(WorkflowParallelExecutionOutcome.Succeeded, (await store.GetParallelExecutionAsync(workflow.Id, "parallel", default))!.Outcome);
        Assert.Single(await db.WorkflowParallelExecutions.ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Activation_identity_is_unique_per_workflow_node_and_cascades_on_delete()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await db.Database.MigrateAsync();
        var store = new EfWorkflowStore(db);
        var workflow = await store.CreateWorkflowAsync(new Workflow { IssueUrl = "https://example.test/issues/2", RepositoryUrl = "https://example.test/repo" }, default);
        await store.UpsertParallelExecutionAsync(new WorkflowParallelExecution { WorkflowId = workflow.Id, NodeId = "parallel" }, default);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => store.UpsertParallelExecutionAsync(new WorkflowParallelExecution { WorkflowId = workflow.Id, NodeId = "parallel" }, default));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        db.ChangeTracker.Clear();
        await store.UpsertParallelExecutionAsync(new WorkflowParallelExecution { WorkflowId = workflow.Id, NodeId = "another" }, default);
        await db.Workflows.Where(item => item.Id == workflow.Id).ExecuteDeleteAsync();
        Assert.Empty(await db.WorkflowParallelExecutions.ToListAsync());
    }

    [Fact]
    public async Task Upgrade_preserves_existing_runs_with_no_attempt_identity()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20260905152037_AddWorkflowLoops");
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO task_runs (\"Id\", \"WorkflowId\", \"Kind\", \"Status\", \"DefinitionStepId\", \"CreatedAt\", \"UpdatedAt\") VALUES ({id}, {Guid.NewGuid()}, 'Plan', 'Succeeded', 'plan', {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow})");
        await db.Database.MigrateAsync();
        var run = await db.TaskRuns.SingleAsync();
        Assert.Equal(id, run.Id);
        Assert.Equal(TaskRunStatus.Succeeded, run.Status);
        Assert.Null(run.ExecutionAttemptId);
        Assert.Empty(await db.WorkflowParallelExecutions.ToListAsync());
    }
}

