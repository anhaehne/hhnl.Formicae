namespace hhnl.Formicae.Application.Workflows;

public sealed partial class WorkflowOrchestrator
{
    private async Task<bool> RunEndNodeAsync(Workflow workflow, WorkflowDefinitionStep step, CancellationToken token, int? visit = null)
    {
        var run = await store.GetTaskRunExecutionAsync(workflow.Id, step.Id, visit, token)
            ?? new TaskRun { WorkflowId = workflow.Id, DefinitionStepId = step.Id, Kind = TaskRunKind.End,
                LoopIteration = visit, CreatedAt = clock.UtcNow };
        run.ExecutionAttemptId ??= Guid.NewGuid();
        run.Status = TaskRunStatus.Succeeded;
        run.StartedAt ??= clock.UtcNow;
        run.CompletedAt ??= clock.UtcNow;
        run.UpdatedAt = clock.UtcNow;
        run.Output = "Workflow completed by End node.";
        await store.UpsertTaskRunAsync(run, token);
        workflow.CurrentDefinitionStepId = step.Id;
        workflow.IsPaused = false;
        await TransitionWorkflowAsync(workflow, WorkflowStatus.Completed, WorkflowStep.Done,
            $"Workflow completed at End node '{step.DisplayName ?? step.Id}'.", token);
        await CancelWorkflowRuntimeAsync(workflow, token, cleanupOnly: true);
        return true;
    }

    private async Task FinalizeStoppedControlExecutionsAsync(Workflow workflow, CancellationToken token)
    {
        var document = await ResolveDefinitionAsync(workflow, token);
        foreach (var execution in await store.ListParallelExecutionsAsync(workflow.Id, token))
            if (execution.Outcome == WorkflowParallelExecutionOutcome.Running)
            {
                // Ordinary graph activations represent the whole workflow; explicit fork regions are interrupted.
                execution.Outcome = document.Steps.Single(step => step.Id == execution.NodeId).Uses == WorkflowParallelDefinitions.Uses
                    ? WorkflowParallelExecutionOutcome.Canceled : WorkflowParallelExecutionOutcome.Succeeded;
                execution.CompletedAt = clock.UtcNow;
                await store.UpsertParallelExecutionAsync(execution, token);
            }
        foreach (var iteration in await store.ListLoopIterationsAsync(workflow.Id, token))
            if (iteration.Outcome == WorkflowLoopIterationOutcome.Running)
            {
                iteration.Outcome = WorkflowLoopIterationOutcome.Canceled;
                iteration.CompletedAt = clock.UtcNow;
                iteration.FailureReason = "Stopped by End node.";
                await store.UpsertLoopIterationAsync(iteration, token);
            }
    }
}
