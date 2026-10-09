namespace hhnl.Formicae.Application.Workflows;

public sealed partial class WorkflowOrchestrator
{
    private async Task<bool> AdvanceDecisionAsync(Workflow workflow, WorkflowDefinitionDocument document,
        WorkflowDefinitionStep node, CancellationToken token)
    {
        var existing = await store.GetDecisionExecutionAsync(workflow.Id, node.Id, token, WorkflowCycleDefinitions.Visit(workflow, node.Id));
        WorkflowDecisionExecution proposed;
        if (existing is not null)
        {
            proposed = existing;
        }
        else
        {
            var condition = node.Decision!.Condition;
            TaskRun? source = condition.Source == "taskOutput" && condition.Reference is not null
                ? await store.GetTaskRunExecutionAsync(workflow.Id, condition.Reference, workflow.CycleExecutionJson is null ? null : WorkflowCycleDefinitions.State(workflow).Active[node.Id].Sources.GetValueOrDefault(condition.Reference), token) : null;
            DecisionEvaluation evaluation;
            try { evaluation = WorkflowDecisionEvaluator.Evaluate(condition, workflow, source); }
            catch (InvalidOperationException exception)
            {
                var message = $"Decision '{node.Id}' could not be evaluated: {exception.Message}";
                await FailWorkflowAsync(workflow, message, new { nodeId = node.Id, code = "decision.evaluation.failed" }, token);
                return true;
            }
            var settings = node.Decision;
            proposed = new WorkflowDecisionExecution
            {
                WorkflowId = workflow.Id, NodeId = node.Id, VisitIteration = WorkflowCycleDefinitions.Visit(workflow, node.Id), BooleanResult = evaluation.Result,
                ConfiguredTargetId = evaluation.Result ? settings.ConfiguredTrueStepId ?? settings.TrueStepId
                    : settings.ConfiguredFalseStepId ?? settings.FalseStepId,
                SelectedTargetId = evaluation.Result ? settings.TrueStepId : settings.FalseStepId,
                InputJson = evaluation.InputJson, SourceTaskRunId = evaluation.SourceTaskRunId, EvaluatedAt = clock.UtcNow
            };
        }
        if (workflow.CycleExecutionJson is not null)
        {
            var saved = workflow.CycleExecutionJson;
            CompleteCycleNode(workflow, document, node.Id, proposed.SelectedTargetId);
            proposed.NextCycleExecutionJson = workflow.CycleExecutionJson;
            workflow.CycleExecutionJson = saved; workflow.CurrentDefinitionStepId = node.Id;
        }
        var next = document.Steps.Single(step => step.Id == proposed.SelectedTargetId);
        var kind = TaskRunKind.Plan;
        if (next.Uses != WorkflowDecisionDefinitions.Uses && next.Uses != WorkflowParallelDefinitions.Uses)
            WorkflowDefinitionValidator.TryMapUsesToTaskKind(next.Uses, out kind);
        WorkflowDecisionCommitResult committed;
        var hasStartedTasks = kind != TaskRunKind.Plan || (await store.ListTaskRunsAsync(workflow.Id, token)).Count > 0;
        var nextStatus = kind == TaskRunKind.Plan && (workflow.Status == WorkflowStatus.Queued || !hasStartedTasks)
            ? WorkflowStatus.Queued : StatusFor(kind);
        try { committed = await store.CommitDecisionAsync(proposed, nextStatus, StepFor(kind), token); }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            // Commit may have succeeded before its response was lost. Resume from the durable row on the next tick.
            await TryLogDecisionWarningAsync(workflow.Id, node.Id, exception, token);
            return false;
        }
        workflow.CurrentDefinitionStepId = committed.Workflow.CurrentDefinitionStepId;
        workflow.CycleExecutionJson = committed.Workflow.CycleExecutionJson;
        workflow.Status = committed.Workflow.Status; workflow.CurrentStep = committed.Workflow.CurrentStep;
        workflow.FailureReason = committed.Workflow.FailureReason; workflow.UpdatedAt = committed.Workflow.UpdatedAt;
        if (committed.Applied)
        {
            try
            {
                await AddEventAsync(workflow.Id, null, "DecisionEvaluated", "Information",
                    $"Decision '{node.Id}' selected {(committed.Execution.BooleanResult ? "True" : "False")} → '{committed.Execution.ConfiguredTargetId}'.",
                    new { nodeId = node.Id, committed.Execution.BooleanResult, committed.Execution.ConfiguredTargetId,
                        committed.Execution.SelectedTargetId, committed.Execution.SourceTaskRunId }, token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
            {
                await TryLogDecisionWarningAsync(workflow.Id, node.Id, exception, token);
            }
        }
        return committed.Applied;
    }

    private async Task TryLogDecisionWarningAsync(Guid workflowId, string nodeId, Exception exception, CancellationToken token)
    {
        try { await store.AddLogAsync(new WorkflowLog { WorkflowId = workflowId, Level = "Warning",
            Message = $"Decision '{nodeId}' will use its durable outcome after an orchestration error: {exception.Message}", CreatedAt = clock.UtcNow }, token); }
        catch (Exception) when (!token.IsCancellationRequested) { }
    }
}
