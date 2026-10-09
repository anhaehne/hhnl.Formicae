using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowCyclePersistenceTests(MigrationPostgresFixture fixture) : IClassFixture<MigrationPostgresFixture>
{
    [Fact]
    public async Task Decision_visits_commit_with_activation_state_and_replays_do_not_rewind()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await db.Database.MigrateAsync();
        var store = new EfWorkflowStore(db);
        var workflow = new Workflow { IssueUrl = "https://example.test/" + Guid.NewGuid(), RepositoryUrl = "https://example.test/repo",
            CurrentDefinitionStepId = "choose", Status = WorkflowStatus.Running };
        SetVisit(workflow, 1);
        await store.CreateWorkflowAsync(workflow, default);
        WorkflowDecisionExecution Outcome(int visit) => new() { WorkflowId = workflow.Id, NodeId = "choose", VisitIteration = visit,
            BooleanResult = true, ConfiguredTargetId = "choose", SelectedTargetId = "choose", InputJson = "{\"value\":true}" };
        var proposed = Outcome(1);
        SetVisit(workflow, 2);
        proposed.NextCycleExecutionJson = workflow.CycleExecutionJson;
        var first = await store.CommitDecisionAsync(proposed, WorkflowStatus.Running, WorkflowStep.Custom, default);
        Assert.True(first.Applied);
        db.ChangeTracker.Clear();
        var restored = (await store.GetWorkflowAsync(workflow.Id, default))!;
        Assert.Equal(2, WorkflowCycleDefinitions.Visit(restored, "choose"));
        Assert.False((await store.CommitDecisionAsync(Outcome(1), WorkflowStatus.Running, WorkflowStep.Custom, default)).Applied);
        Assert.Equal(2, WorkflowCycleDefinitions.Visit((await store.GetWorkflowAsync(workflow.Id, default))!, "choose"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitDecisionAsync(Outcome(3), WorkflowStatus.Running, WorkflowStep.Custom, default));
        var second = Outcome(2); SetVisit(restored, 3); second.NextCycleExecutionJson = restored.CycleExecutionJson;
        Assert.True((await store.CommitDecisionAsync(second, WorkflowStatus.Running, WorkflowStep.Custom, default)).Applied);
        Assert.Equal(new int?[] { 1, 2 }, (await store.ListDecisionExecutionsAsync(workflow.Id, default)).Select(item => item.VisitIteration));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Parallel_visits_have_independent_snapshots_and_duplicate_visit_is_rejected()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await db.Database.MigrateAsync();
        var store = new EfWorkflowStore(db);
        var workflow = await store.CreateWorkflowAsync(new() { IssueUrl = "https://example.test/" + Guid.NewGuid(), RepositoryUrl = "https://example.test/repo" }, default);
        foreach (var visit in new[] { 1, 2 })
            await store.UpsertParallelExecutionAsync(new() { WorkflowId = workflow.Id, NodeId = "group", VisitIteration = visit, EntryPlanArtifact = $"snapshot {visit}" }, default);
        db.ChangeTracker.Clear();
        Assert.Equal("snapshot 1", (await store.GetParallelExecutionAsync(workflow.Id, "group", default, 1))!.EntryPlanArtifact);
        Assert.Equal("snapshot 2", (await store.GetParallelExecutionAsync(workflow.Id, "group", default, 2))!.EntryPlanArtifact);
        Assert.Equal(2, (await store.ListParallelExecutionsAsync(workflow.Id, default)).Count);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.UpsertParallelExecutionAsync(new() { WorkflowId = workflow.Id, NodeId = "group", VisitIteration = 2 }, default));
    }

    private static void SetVisit(Workflow workflow, int visit)
    {
        var state = new WorkflowCycleState { Entry = "choose" };
        state.Completed["choose"] = visit - 1;
        state.Active["choose"] = new(visit, new() { ["source"] = visit - 1 }, DateTimeOffset.UtcNow);
        WorkflowCycleDefinitions.Save(workflow, state);
    }
}
