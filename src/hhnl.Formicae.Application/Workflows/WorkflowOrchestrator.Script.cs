using System.Text.Json;

namespace hhnl.Formicae.Application.Workflows;

public sealed partial class WorkflowOrchestrator
{
    private async Task<bool> RunScriptTaskAsync(Workflow workflow, WorkflowDefinitionStep step, CancellationToken token)
    {
        var run = await GetCurrentTaskRunAsync(workflow, token) ?? await CreateCurrentTaskRunAsync(workflow, TaskRunKind.Script, token);
        try
        {
            if (run.Status == TaskRunStatus.Succeeded) { await AdvanceDefinitionCursorAsync(workflow, "Script task completed.", token); return true; }
            if (run.Status == TaskRunStatus.Failed) { await FailWorkflowAsync(workflow, run.FailureReason ?? "Script task failed.", null, token); return true; }
            if (run.Status == TaskRunStatus.Running && !string.IsNullOrWhiteSpace(run.ExternalId))
            {
                var result = await agentRunner.TryGetResultAsync(run.ExternalId, token);
                if (result is null) return false;
                await CompleteScriptTaskAsync(workflow, run, result, token); return true;
            }
            PreparedAgentTask prepared;
            try
            {
                var script = step.Script ?? throw new InvalidOperationException("Script task settings are missing.");
                run.ExecutionAttemptId ??= Guid.NewGuid();
                prepared = await PrepareAgentTaskAsync(workflow, run, new AgentTask(workflow.Id, TaskRunKind.Script,
                    "", workflow.RepositoryUrl, workflow.BranchName ?? workflow.BaseBranch, null,
                    ExecutionAttemptId: run.ExecutionAttemptId, TimeoutSeconds: script.TimeoutSeconds, Script: script), token);
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException or ArgumentException)
            {
                await CompleteTaskRunAsync(workflow, run, "", false, exception.Message, token);
                await FailWorkflowAsync(workflow, exception.Message, null, token); return true;
            }
            // Persist the attempt before a deterministic worker launch; uncertain responses reattach to it.
            await StartTaskRunAsync(workflow, run, token);
            if (workflow.Status != WorkflowStatus.Running) await TransitionWorkflowAsync(workflow, WorkflowStatus.Running, WorkflowStep.Script, "Script task started.", token);
            AgentRunStartResult started;
            try { started = await agentRunner.StartAsync(prepared.Task, token); }
            catch (Exception exception) when (IsUncertainParallelTransport(exception, token)) { await AddCustomWarningAsync(workflow, run, exception, token); return false; }
            catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
            {
                await CompleteTaskRunAsync(workflow, run, "", false, exception.Message, token);
                await FailWorkflowAsync(workflow, exception.Message, null, token); return true;
            }
            if (started.CompletedResult is not null) await CompleteScriptTaskAsync(workflow, run, started.CompletedResult, token);
            else await AssignExternalJobAsync(workflow, run, started.ExternalId, token);
            await AddEventAsync(workflow.Id, run.Id, "AgentSettingsResolved", "Information", "Script execution settings resolved.",
                new { executionAttemptId = run.ExecutionAttemptId, externalId = started.ExternalId,
                    timeoutSeconds = prepared.Task.TimeoutSeconds, environment = EnvironmentAudit(prepared.Task.EnvironmentSnapshot, prepared.Task.Capabilities),
                    capabilities = prepared.Task.Capabilities, secretReferences = prepared.Task.SecretReferences,
                    shell = prepared.Task.Script!.Shell, workingDirectory = prepared.Task.Script.WorkingDirectory }, token);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        { await AddCustomWarningAsync(workflow, run, exception, token); return false; }
    }

    private async Task CompleteScriptTaskAsync(Workflow workflow, TaskRun run, AgentRunResult result, CancellationToken token)
    {
        // stdout is an explicit string output; the full evidence remains even when a consumer's input is too small.
        run.StructuredOutputsJson = result.Succeeded ? JsonSerializer.Serialize(new { output = result.Output }, CustomExecutionJsonOptions) : null;
        await CompleteTaskRunAsync(workflow, run, result, token);
        await AddAgentOutputLogAsync(workflow.Id, run, result, token);
        if (result.Succeeded) await AdvanceDefinitionCursorAsync(workflow, "Script task completed.", token);
        else await FailWorkflowAsync(workflow, result.FailureReason ?? "Script task failed.", BuildFailureDetails(run, result), token);
    }
}
