using System.Security.Cryptography;
using System.Text;

namespace hhnl.Formicae.Application.Workflows;

public sealed partial class WorkflowOrchestrator
{
    private sealed class RuntimeEvidenceUnavailableException(string message, Exception inner) : Exception(message, inner);

    private async Task CaptureRuntimeLogsAsync(Workflow workflow, TaskRun run, CancellationToken token, bool cancellation = false)
    {
        if (run.RuntimeLogsCaptured || string.IsNullOrWhiteSpace(run.ExternalId)) return;
        try
        {
            var runtimeLogs = await agentRunner.ReadLogsAsync(run.ExternalId, token);
            var logs = runtimeLogs.Select(item => new WorkflowLog
            {
                Id = item.MessageId ?? Guid.NewGuid(), WorkflowId = workflow.Id, TaskRunId = run.Id,
                ExecutionAttemptId = run.ExecutionAttemptId, ExternalId = run.ExternalId,
                Source = item.Source, SourceSequence = item.SourceSequence,
                Level = item.Source == "worker-error" ? "Error" : item.Source == "stderr" ? "Warning" : "Information",
                Message = item.Message, CreatedAt = item.Timestamp
            }).ToList();
            if (logs.Count > 0 || cancellation)
                logs.Add(new WorkflowLog
                {
                    Id = EvidenceId(run, cancellation ? "cancel-capture" : "runtime-capture"), WorkflowId = workflow.Id,
                    TaskRunId = run.Id, ExecutionAttemptId = run.ExecutionAttemptId, ExternalId = run.ExternalId,
                    Source = "system", CreatedAt = clock.UtcNow,
                    Message = cancellation
                        ? "Available worker logs saved before cancellation. Output produced during termination may be unavailable."
                        : "Runtime log recovery completed; worker message identities prevent duplicate callback lines. Legacy lines are labeled runtime."
                });
            await store.AppendLogsAsync(logs, token);
            run.RuntimeLogsCaptured = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        { throw new RuntimeEvidenceUnavailableException("Runtime logs could not be saved; worker cleanup will retry after recovery.", exception); }
    }

    private static Guid EvidenceId(TaskRun run, string purpose)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{run.Id}:{run.ExecutionAttemptId}:{run.ExternalId}:{purpose}")).AsSpan(0, 16));

    private async Task TryAcknowledgeRuntimeAsync(Workflow workflow, TaskRun run, CancellationToken token)
    {
        if (!run.RuntimeCleanupPending || run.Status is TaskRunStatus.Running or TaskRunStatus.Queued) return;
        try
        {
            await CaptureRuntimeLogsAsync(workflow, run, token);
            // Persist capture acknowledgement BEFORE any destructive runtime operation.
            await store.UpsertTaskRunAsync(run, token);
            if (run.ExternalId is not null) await agentRunner.AcknowledgeCompletionAsync(run.ExternalId, token);
            run.RuntimeCleanupPending = false;
            await store.UpsertTaskRunAsync(run, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            run.RuntimeCleanupPending = true;
            await RecordRuntimeWarningAsync(workflow, run, exception, token);
        }
    }

    private async Task ReconcileRuntimeCleanupAsync(Workflow workflow, CancellationToken token)
    {
        foreach (var run in await store.ListTaskRunsAsync(workflow.Id, token))
            if (run.RuntimeCleanupPending && run.Status is not (TaskRunStatus.Running or TaskRunStatus.Queued))
                await TryAcknowledgeRuntimeAsync(workflow, run, token);
    }

    private async Task<bool> CancelWorkflowRuntimeAsync(Workflow workflow, CancellationToken token, bool cleanupOnly = false)
    {
        await store.CancelWaitsAsync(workflow.Id, token);
        var allStopped = true;
        foreach (var run in await store.ListTaskRunsAsync(workflow.Id, token))
        {
            if (run.Status is not (TaskRunStatus.Running or TaskRunStatus.Queued or TaskRunStatus.Waiting)) continue;
            try
            {
                // A crash may happen after launch acceptance and before recording the external ID.
                run.ExternalId ??= run.Status == TaskRunStatus.Running
                    ? agentRunner.ResolveExternalId(workflow.Id, run.Kind, run.ExecutionAttemptId) : null;
                if (run.Status == TaskRunStatus.Running && run.ExternalId is null)
                    throw new InvalidOperationException("The running worker identity is unavailable; cancellation remains pending.");
                if (run.ExternalId is not null)
                {
                    await CaptureRuntimeLogsAsync(workflow, run, token, cancellation: true);
                    run.RuntimeCleanupPending = true;
                    await store.UpsertTaskRunAsync(run, token);
                    await agentRunner.CancelAsync(run.ExternalId, token);
                }
                run.Status = cleanupOnly && workflow.Status == WorkflowStatus.Failed ? TaskRunStatus.Failed : TaskRunStatus.Canceled;
                run.CompletedAt = clock.UtcNow;
                run.UpdatedAt = clock.UtcNow;
                run.FailureReason = cleanupOnly ? "Worker terminated after workflow completion or failure." : "Canceled by workflow cancellation.";
                await store.UpsertTaskRunAsync(run, token);
                await AddEventAsync(workflow.Id, run.Id, "TaskCanceled", "Information", "Worker task canceled.",
                    new { run.ExternalId, run.ExecutionAttemptId }, token);
                await TryAcknowledgeRuntimeAsync(workflow, run, token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
            {
                allStopped = false;
                await RecordRuntimeWarningAsync(workflow, run, exception, token);
            }
        }
        if (!allStopped) return false;
        if (cleanupOnly) return true;
        workflow.CancelCompletedAt = clock.UtcNow;
        workflow.IsPaused = false;
        await TransitionWorkflowAsync(workflow, workflow.Status == WorkflowStatus.Failed ? WorkflowStatus.Failed : WorkflowStatus.Canceled, WorkflowStep.Done, "Workflow cancellation completed.", token);
        return true;
    }

    private async Task<bool> PollPausedTasksAsync(Workflow workflow, CancellationToken token)
    {
        try
        {
            var running = (await store.ListTaskRunsAsync(workflow.Id, token)).Where(run => run.Status == TaskRunStatus.Running).ToArray();
            await MatchPausedWaitsAsync(workflow, token);
            if (running.Length == 0) return false;
            var definition = await ResolveDefinitionAsync(workflow, token);
            if (WorkflowGraphDefinitions.IsGraph(definition))
            {
                var changed = false;
                foreach (var run in running)
                {
                    var result = await TryGetRunningAgentResultAsync(run, token);
                    if (result is null) continue;
                    if (run.Kind == TaskRunKind.Plan) result = ValidatePlanningResult(result);
                    if (run.Kind == TaskRunKind.Custom) result = await ValidateCustomTaskResultAsync(workflow, run, result, token);
                    if (run.Kind == TaskRunKind.Script && result.Succeeded)
                        run.StructuredOutputsJson = System.Text.Json.JsonSerializer.Serialize(new { output = result.Output }, CustomExecutionJsonOptions);
                    await CompleteTaskRunAsync(workflow, run, result, token);
                    await AddAgentOutputLogAsync(workflow.Id, run, result, token);
                    if (run.Kind == TaskRunKind.AddressComments && result.Succeeded)
                        await sourceControl.UpsertPullRequestCommentAsync(workflow, PullRequestCommentMarkers.BuildAddressCommentsBody(workflow, result), token);
                    changed = true;
                }
                return changed;
            }
            var current = definition.Steps.SingleOrDefault(step => step.Id == workflow.CurrentDefinitionStepId);
            if (current?.Uses == WorkflowParallelDefinitions.Uses)
            {
                var changed = false;
                foreach (var run in running)
                {
                    var result = await TryGetRunningAgentResultAsync(run, token);
                    if (result is null) continue;
                    result = ValidatePlanningResult(result);
                    await CompleteTaskRunAsync(workflow, run, result, token);
                    await AddAgentOutputLogAsync(workflow.Id, run, result, token);
                    changed = true;
                }
                return changed;
            }
            // Existing task completion may advance the cursor, but this path cannot launch another task.
            var active = running.SingleOrDefault(run => run.DefinitionStepId == workflow.CurrentDefinitionStepId) ?? running.First();
            if (active.ExternalId is null && active.ExecutionAttemptId is not null)
            {
                active.ExternalId = agentRunner.ResolveExternalId(workflow.Id, active.Kind, active.ExecutionAttemptId);
                if (active.ExternalId is not null) await store.UpsertTaskRunAsync(active, token);
            }
            return active.Kind switch
            {
                TaskRunKind.Plan => await RunPlanningAsync(workflow, null, token),
                TaskRunKind.Implement => await RunImplementationAsync(workflow, token),
                TaskRunKind.AddressComments => await AddressPullRequestCommentsAsync(workflow, token),
                TaskRunKind.Script when current is not null && active.ExternalId is not null => await RunScriptTaskAsync(workflow, current, token),
                TaskRunKind.Custom when current is not null && active.ExternalId is not null => await RunCustomTaskAsync(workflow, current, token),
                _ => false
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            await RecordRuntimeWarningAsync(workflow, null, exception, token);
            return false;
        }
    }

    private Task RecordRuntimeWarningAsync(Workflow workflow, TaskRun? run, Exception exception, CancellationToken token)
        => store.AddLogAsync(new WorkflowLog
        {
            WorkflowId = workflow.Id, TaskRunId = run?.Id, ExecutionAttemptId = run?.ExecutionAttemptId,
            ExternalId = run?.ExternalId, Level = "Warning", Source = "system", CreatedAt = clock.UtcNow,
            Message = $"Runtime reconciliation will retry: {exception.Message}"
        }, token);
}
