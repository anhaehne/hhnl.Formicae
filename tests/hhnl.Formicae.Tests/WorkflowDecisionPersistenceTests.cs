using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowDecisionPersistenceTests(MigrationPostgresFixture fixture) : IClassFixture<MigrationPostgresFixture>
{
    [Fact]
    public async Task Outcome_and_cursor_are_durable_and_replay_never_rewinds_downstream()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await db.Database.MigrateAsync();
        var store = new EfWorkflowStore(db); var workflow = await CreateAsync(store);
        var proposed = Outcome(workflow.Id, true);
        var committed = await store.CommitDecisionAsync(proposed, WorkflowStatus.Planning, WorkflowStep.Plan, default);
        Assert.True(committed.Applied); Assert.Equal("yes", committed.Workflow.CurrentDefinitionStepId);
        db.ChangeTracker.Clear();
        var stored = (await store.GetWorkflowAsync(workflow.Id, default))!;
        Assert.Equal("yes", stored.CurrentDefinitionStepId);
        Assert.Equal(proposed.Id, (await store.GetDecisionExecutionAsync(workflow.Id, "choose", default))!.Id);
        stored.CurrentDefinitionStepId = "converged"; await store.UpdateWorkflowAsync(stored, default);
        var replay = await store.CommitDecisionAsync(Outcome(workflow.Id, false), WorkflowStatus.Planning, WorkflowStep.Plan, default);
        Assert.False(replay.Applied); Assert.True(replay.Execution.BooleanResult);
        Assert.Equal("converged", replay.Workflow.CurrentDefinitionStepId);
        Assert.Single(await store.ListDecisionExecutionsAsync(workflow.Id, default));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Competing_decisions_commit_one_outcome_without_forking()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await db.Database.MigrateAsync();
        var workflow = await CreateAsync(new(db));
        async Task<WorkflowDecisionCommitResult> Commit(bool result)
        {
            await using var competing = new FormicaeDbContext(new DbContextOptionsBuilder<FormicaeDbContext>().UseNpgsql(db.Database.GetConnectionString()).Options);
            return await new EfWorkflowStore(competing).CommitDecisionAsync(Outcome(workflow.Id, result), WorkflowStatus.Planning, WorkflowStep.Plan, default);
        }
        var results = await Task.WhenAll(Commit(true), Commit(false));
        Assert.Single(results, result => result.Applied);
        Assert.Equal(results[0].Execution.Id, results[1].Execution.Id);
        var persisted = await db.Workflows.AsNoTracking().SingleAsync();
        Assert.Equal(results[0].Execution.SelectedTargetId, persisted.CurrentDefinitionStepId);
    }

    [Fact]
    public async Task Failed_cursor_update_rolls_back_outcome_and_detaches_pending_insert()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await db.Database.MigrateAsync();
        var store = new EfWorkflowStore(db); var workflow = await CreateAsync(store);
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_decision_update() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test update failure'; END; $$; CREATE TRIGGER reject_decision_update BEFORE UPDATE ON workflows FOR EACH ROW EXECUTE FUNCTION reject_decision_update();");
        await Assert.ThrowsAnyAsync<Exception>(() => store.CommitDecisionAsync(Outcome(workflow.Id, true), WorkflowStatus.Planning, WorkflowStep.Plan, default));
        await store.AddLogAsync(new WorkflowLog { WorkflowId = workflow.Id, Message = "warning after rollback" }, default);
        Assert.Empty(await db.WorkflowDecisionExecutions.AsNoTracking().ToListAsync());
        Assert.Equal("choose", (await db.Workflows.AsNoTracking().SingleAsync()).CurrentDefinitionStepId);
    }

    [Fact]
    public async Task Stale_cursor_cannot_create_a_new_outcome()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await db.Database.MigrateAsync();
        var store = new EfWorkflowStore(db); var workflow = await CreateAsync(store);
        workflow.CurrentDefinitionStepId = "elsewhere"; await store.UpdateWorkflowAsync(workflow, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitDecisionAsync(Outcome(workflow.Id, true), WorkflowStatus.Planning, WorkflowStep.Plan, default));
        Assert.Empty(await store.ListDecisionExecutionsAsync(workflow.Id, default));
    }

    private static Task<Workflow> CreateAsync(EfWorkflowStore store) => store.CreateWorkflowAsync(new Workflow
    { IssueUrl = "https://example.test/" + Guid.NewGuid(), RepositoryUrl = "https://example.test/repo", CurrentDefinitionStepId = "choose", Status = WorkflowStatus.Planning }, default);
    private static WorkflowDecisionExecution Outcome(Guid id, bool result) => new()
    { WorkflowId = id, NodeId = "choose", BooleanResult = result, ConfiguredTargetId = result ? "yes" : "no", SelectedTargetId = result ? "yes" : "no", InputJson = "{\"value\":true}" };
}
