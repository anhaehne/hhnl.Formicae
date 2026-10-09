using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowEndPersistenceTests(MigrationPostgresFixture fixture) : IClassFixture<MigrationPostgresFixture>
{
    [Fact]
    public async Task Completed_workflow_remains_runnable_for_wait_and_queued_cleanup_then_cannot_claim_events()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await db.Database.MigrateAsync();
        var store = new EfWorkflowStore(db);
        var workflow = await store.CreateWorkflowAsync(new() { IssueUrl = "https://github.com/acme/repo/issues/1", RepositoryUrl = "https://github.com/acme/repo", Status = WorkflowStatus.Completed }, default);
        await store.UpsertTaskRunAsync(new() { WorkflowId = workflow.Id, DefinitionStepId = "end", Kind = TaskRunKind.End, Status = TaskRunStatus.Succeeded }, default);
        var run = await store.UpsertTaskRunAsync(new() { WorkflowId = workflow.Id, Kind = TaskRunKind.Wait, Status = TaskRunStatus.Waiting,
            DefinitionStepId = "wait", ExecutionAttemptId = Guid.NewGuid() }, default);
        var wait = await store.ArmWaitAsync(new() { WorkflowId = workflow.Id, TaskRunId = run.Id, ExecutionAttemptId = run.ExecutionAttemptId!.Value,
            Uses = "github.issue-commented", Provider = "GitHub", RepositoryUrl = workflow.RepositoryUrl, IssueUrl = workflow.IssueUrl, ArmedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, default);
        db.ChangeTracker.Clear();
        Assert.Contains(await store.ListRunnableWorkflowsAsync(default), item => item.Id == workflow.Id);
        await store.CancelWaitsAsync(workflow.Id, default);
        await store.AcceptWaitEventAsync(new() { Provider = "GitHub", DeliveryId = "late", EventSequence = 1, EventKey = "late-comment",
            Uses = wait.Uses, RepositoryUrl = wait.RepositoryUrl, IssueUrl = wait.IssueUrl, CreatedAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow, OutputsJson = "{}" }, default);
        Assert.Null(await store.ClaimWaitEventAsync(wait.Id, default));
        run.Status = TaskRunStatus.Canceled; await store.UpsertTaskRunAsync(run, default);
        var queued = await store.UpsertTaskRunAsync(new() { WorkflowId = workflow.Id, DefinitionStepId = "queued", Kind = TaskRunKind.Plan }, default);
        Assert.Contains(await store.ListRunnableWorkflowsAsync(default), item => item.Id == workflow.Id);
        queued.Status = TaskRunStatus.Canceled; await store.UpsertTaskRunAsync(queued, default);
        Assert.DoesNotContain(await store.ListRunnableWorkflowsAsync(default), item => item.Id == workflow.Id);
    }

    [Fact]
    public async Task Interrupted_parallel_finalization_is_runnable_and_canceled_outcome_round_trips()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await db.Database.MigrateAsync();
        var store = new EfWorkflowStore(db);
        var workflow = await store.CreateWorkflowAsync(new() { IssueUrl = "issue", RepositoryUrl = "repo", Status = WorkflowStatus.Completed }, default);
        await store.UpsertTaskRunAsync(new() { WorkflowId = workflow.Id, DefinitionStepId = "end", Kind = TaskRunKind.End, Status = TaskRunStatus.Succeeded }, default);
        var execution = await store.UpsertParallelExecutionAsync(new() { WorkflowId = workflow.Id, NodeId = "fork" }, default);
        db.ChangeTracker.Clear();
        Assert.Contains(await store.ListRunnableWorkflowsAsync(default), item => item.Id == workflow.Id);
        execution.Outcome = WorkflowParallelExecutionOutcome.Canceled; execution.CompletedAt = DateTimeOffset.UtcNow;
        await store.UpsertParallelExecutionAsync(execution, default);
        db.ChangeTracker.Clear();
        Assert.Equal(WorkflowParallelExecutionOutcome.Canceled, (await store.GetParallelExecutionAsync(workflow.Id, "fork", default))!.Outcome);
        Assert.DoesNotContain(await store.ListRunnableWorkflowsAsync(default), item => item.Id == workflow.Id);
    }
}
