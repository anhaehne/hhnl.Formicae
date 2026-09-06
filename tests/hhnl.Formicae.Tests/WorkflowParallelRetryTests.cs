using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowParallelRetryTests
{
    [Fact]
    public async Task Workflow_retry_resets_all_failed_branches_and_preserves_successful_work()
    {
        var (store, workflow, execution, runs) = await CreateAsync();
        var oldAttempts = runs.Select(run => run.ExecutionAttemptId).ToArray();
        var result = await new WorkflowService(store).RetryWorkflowAsync(workflow.Id, default);
        Assert.Equal(WorkflowStatus.Planning, result!.Status);
        Assert.Equal("fork", workflow.CurrentDefinitionStepId);
        Assert.Equal(WorkflowParallelExecutionOutcome.Running, execution.Outcome);
        Assert.Null(execution.CompletedAt);
        Assert.Equal("entry snapshot", execution.EntryPlanArtifact);
        foreach (var run in runs.Take(2))
        {
            Assert.Equal(TaskRunStatus.Queued, run.Status);
            Assert.Null(run.ExternalId);
            Assert.Null(run.Output);
            Assert.Null(run.FailureReason);
            Assert.Null(run.CompletedAt);
            Assert.DoesNotContain(run.ExecutionAttemptId, oldAttempts);
        }
        Assert.Equal(TaskRunStatus.Succeeded, runs[2].Status);
        Assert.Equal(oldAttempts[2], runs[2].ExecutionAttemptId);
        Assert.Equal("preserved output", runs[2].Output);
    }

    [Fact]
    public async Task Task_retry_resets_only_selected_branch_and_keeps_group_cursor()
    {
        var (store, workflow, execution, runs) = await CreateAsync();
        var otherAttempt = runs[1].ExecutionAttemptId;
        await new WorkflowService(store).RetryTaskRunAsync(workflow.Id, runs[0].Id, default);
        Assert.Equal(TaskRunStatus.Queued, runs[0].Status);
        Assert.Equal(TaskRunStatus.Failed, runs[1].Status);
        Assert.Equal(otherAttempt, runs[1].ExecutionAttemptId);
        Assert.Equal(TaskRunStatus.Succeeded, runs[2].Status);
        Assert.Equal("fork", workflow.CurrentDefinitionStepId);
        Assert.Equal(WorkflowParallelExecutionOutcome.Running, execution.Outcome);
    }

    [Fact]
    public async Task Active_group_rejects_retry_of_unrelated_historical_task()
    {
        var (store, workflow, _, _) = await CreateAsync();
        var run = await store.UpsertTaskRunAsync(new TaskRun
        {
            WorkflowId = workflow.Id, DefinitionStepId = "before", Kind = TaskRunKind.Plan,
            Status = TaskRunStatus.Failed
        }, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowService(store)
            .RetryTaskRunAsync(workflow.Id, run.Id, default));
        Assert.Equal(TaskRunStatus.Failed, run.Status);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status);
    }

    [Fact]
    public async Task Join_recovery_does_not_retry_unrelated_historical_failure()
    {
        var (store, workflow, execution, runs) = await CreateAsync();
        foreach (var run in runs) run.Status = TaskRunStatus.Succeeded;
        execution.Outcome = WorkflowParallelExecutionOutcome.Succeeded;
        var oldRun = await store.UpsertTaskRunAsync(new TaskRun
        {
            WorkflowId = workflow.Id, DefinitionStepId = "before", Kind = TaskRunKind.Plan,
            Status = TaskRunStatus.Failed
        }, default);
        await new WorkflowService(store).RetryWorkflowAsync(workflow.Id, default);
        Assert.Equal(WorkflowStatus.Planning, workflow.Status);
        Assert.Equal("fork", workflow.CurrentDefinitionStepId);
        Assert.Equal(WorkflowParallelExecutionOutcome.Succeeded, execution.Outcome);
        Assert.Equal(TaskRunStatus.Failed, oldRun.Status);
        Assert.All(runs, run => Assert.Equal(TaskRunStatus.Succeeded, run.Status));
    }

    private static async Task<(InMemoryWorkflowStore, Workflow, WorkflowParallelExecution, TaskRun[])> CreateAsync()
    {
        var store = new InMemoryWorkflowStore();
        var definition = await store.CreateWorkflowDefinitionAsync(new() { Name = "Parallel retry" }, default);
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "fork",
        [
            new("fork", WorkflowParallelDefinitions.Uses, "after", Parallel: new(["left", "right", "done"])),
            new("left", "builtins.plan", "fork", NextStepPort: "join"),
            new("right", "builtins.plan", "fork", NextStepPort: "join"),
            new("done", "builtins.plan", "fork", NextStepPort: "join"),
            new("after", "builtins.implement")
        ]);
        var version = await store.CreateWorkflowDefinitionVersionAsync(new()
        {
            WorkflowDefinitionId = definition.Id, Version = 1, DslSchemaVersion = document.Schema,
            IsEnabled = true, DefinitionJson = WorkflowDefinitionJson.Serialize(document)
        }, default);
        var workflow = await store.CreateWorkflowAsync(new()
        {
            IssueUrl = "https://example.test/issues/1", RepositoryUrl = "https://example.test/repo",
            WorkflowDefinitionId = definition.Id, WorkflowDefinitionVersionId = version.Id,
            CurrentDefinitionStepId = "fork", CurrentStep = WorkflowStep.Plan, Status = WorkflowStatus.Failed
        }, default);
        var execution = await store.UpsertParallelExecutionAsync(new()
        {
            WorkflowId = workflow.Id, NodeId = "fork", EntryPlanArtifact = "entry snapshot",
            Outcome = WorkflowParallelExecutionOutcome.Failed, CompletedAt = DateTimeOffset.UtcNow
        }, default);
        var runs = new List<TaskRun>();
        foreach (var id in new[] { "left", "right", "done" })
        {
            runs.Add(await store.UpsertTaskRunAsync(new()
            {
                WorkflowId = workflow.Id, DefinitionStepId = id, Kind = TaskRunKind.Plan,
                ExecutionAttemptId = Guid.NewGuid(), ExternalId = $"job-{id}",
                Status = id == "done" ? TaskRunStatus.Succeeded : TaskRunStatus.Failed,
                Output = "preserved output", FailureReason = id == "done" ? null : "failed",
                CompletedAt = DateTimeOffset.UtcNow
            }, default));
        }
        return (store, workflow, execution, runs.ToArray());
    }
}
