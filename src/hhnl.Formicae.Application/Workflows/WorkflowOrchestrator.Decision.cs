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
            var settings = node.Decision!;
            try
            {
                string selectedTarget; string configuredTarget; string inputJson; bool booleanResult; Guid? sourceRunId;
                if (settings.InputType is not null)
                {
                    var sources = await ResolveInputProvenanceAsync(workflow, new TaskRun(), node, document, token);
                    var source = sources["value"];
                    var evaluation = WorkflowDecisionEvaluator.EvaluateInput(settings, source);
                    selectedTarget = evaluation.Target; inputJson = evaluation.InputJson; booleanResult = evaluation.BooleanResult;
                    sourceRunId = source.RunId == Guid.Empty ? null : source.RunId;
                    // Read configured targets from the immutable version; normalization may compile a Loop entry.
                    var version = workflow.WorkflowDefinitionVersionId is { } versionId ? await store.GetWorkflowDefinitionVersionAsync(versionId, token) : null;
                    var original = version is null ? settings : WorkflowDefinitionJson.Deserialize(version.DefinitionJson)?.Steps.FirstOrDefault(step => step.Id == node.Id)?.Decision ?? settings;
                    using var evidence = System.Text.Json.JsonDocument.Parse(inputJson);
                    var port = evidence.RootElement.GetProperty("selectedPort").GetString();
                    configuredTarget = WorkflowDecisionDefinitions.Routes(original).First(route => route.Port == port).Target;
                }
                else
                {
                    var condition = settings.Condition;
                    TaskRun? source = condition.Source == "taskOutput" && condition.Reference is not null
                        ? await store.GetTaskRunExecutionAsync(workflow.Id, condition.Reference, workflow.CycleExecutionJson is null ? null : WorkflowCycleDefinitions.State(workflow).Active[node.Id].Sources.GetValueOrDefault(condition.Reference), token) : null;
                    var evaluation = WorkflowDecisionEvaluator.Evaluate(condition, workflow, source);
                    booleanResult = evaluation.Result; inputJson = evaluation.InputJson; sourceRunId = evaluation.SourceTaskRunId;
                    selectedTarget = booleanResult ? settings.TrueStepId : settings.FalseStepId;
                    configuredTarget = booleanResult ? settings.ConfiguredTrueStepId ?? settings.TrueStepId : settings.ConfiguredFalseStepId ?? settings.FalseStepId;
                }
                proposed = new WorkflowDecisionExecution
                {
                    WorkflowId = workflow.Id, NodeId = node.Id, VisitIteration = WorkflowCycleDefinitions.Visit(workflow, node.Id), BooleanResult = booleanResult,
                    ConfiguredTargetId = configuredTarget, SelectedTargetId = selectedTarget,
                    InputJson = inputJson, SourceTaskRunId = sourceRunId, EvaluatedAt = clock.UtcNow
                };
            }
            catch (InvalidOperationException exception)
            {
                var message = $"Decision '{node.Id}' could not be evaluated: {exception.Message}";
                await FailWorkflowAsync(workflow, message, new { nodeId = node.Id, code = "decision.evaluation.failed" }, token);
                return true;
            }
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
                    $"Decision '{node.Id}' selected route → '{committed.Execution.ConfiguredTargetId}'.",
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
