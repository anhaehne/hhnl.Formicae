using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowDecisionRetryTests
{
    [Fact]
    public async Task Evaluation_retry_preserves_cursor_and_does_not_requeue_historical_task()
    {
        var (store, workflow, historical) = await CreateAsync("decision");
        await new WorkflowService(store).RetryWorkflowAsync(workflow.Id, default);
        Assert.Equal("decision", workflow.CurrentDefinitionStepId);
        Assert.Equal(WorkflowStatus.Planning, workflow.Status);
        Assert.Equal(TaskRunStatus.Failed, historical.Status);
        Assert.Empty(await store.ListDecisionExecutionsAsync(workflow.Id, default));
    }

    [Fact]
    public async Task Task_retry_cannot_reactivate_another_decision_arm()
    {
        var (store, workflow, historical) = await CreateAsync("right");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowService(store)
            .RetryTaskRunAsync(workflow.Id, historical.Id, default));
        Assert.Equal("right", workflow.CurrentDefinitionStepId);
        Assert.Equal(TaskRunStatus.Failed, historical.Status);
    }

    [Fact]
    public async Task Current_selected_task_can_be_retried_without_changing_route()
    {
        var (store, workflow, selected) = await CreateAsync("left");
        await new WorkflowService(store).RetryTaskRunAsync(workflow.Id, selected.Id, default);
        Assert.Equal("left", workflow.CurrentDefinitionStepId);
        Assert.Equal(TaskRunStatus.Queued, selected.Status);
        Assert.NotNull(selected.ExecutionAttemptId);
    }

    private static async Task<(InMemoryWorkflowStore, Workflow, TaskRun)> CreateAsync(string cursor)
    {
        var store = new InMemoryWorkflowStore();
        var definition = await store.CreateWorkflowDefinitionAsync(new() { Name = "Decision retry" }, default);
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "decision",
        [
            new("decision", WorkflowDecisionDefinitions.Uses, Decision: new(
                new("literal", "boolean", "equals", Value: JsonSerializer.SerializeToElement(true), CompareTo: JsonSerializer.SerializeToElement(true)), "left", "right")),
            new("left", "builtins.plan"), new("right", "builtins.plan")
        ]);
        var version = await store.CreateWorkflowDefinitionVersionAsync(new()
        {
            WorkflowDefinitionId = definition.Id, Version = 1, DslSchemaVersion = document.Schema,
            DefinitionJson = WorkflowDefinitionJson.Serialize(document), IsEnabled = true
        }, default);
        var workflow = await store.CreateWorkflowAsync(new()
        {
            IssueUrl = "https://example.test/issues/1", RepositoryUrl = "https://example.test/repo",
            WorkflowDefinitionId = definition.Id, WorkflowDefinitionVersionId = version.Id,
            CurrentDefinitionStepId = cursor, Status = WorkflowStatus.Failed, CurrentStep = WorkflowStep.Plan
        }, default);
        var run = await store.UpsertTaskRunAsync(new()
        {
            WorkflowId = workflow.Id, DefinitionStepId = "left", Kind = TaskRunKind.Plan, Status = TaskRunStatus.Failed
        }, default);
        return (store, workflow, run);
    }
}
