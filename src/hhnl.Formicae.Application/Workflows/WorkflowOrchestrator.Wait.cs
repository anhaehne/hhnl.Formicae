using System.Text.Json;

namespace hhnl.Formicae.Application.Workflows;

public sealed partial class WorkflowOrchestrator
{
    private async Task<bool> RunWaitNodeAsync(Workflow workflow, WorkflowDefinitionStep step, CancellationToken token)
    {
        var run = await GetCurrentTaskRunAsync(workflow, token) ?? await CreateCurrentTaskRunAsync(workflow, TaskRunKind.Wait, token);
        if (run.Status == TaskRunStatus.Succeeded)
        {
            await AdvanceDefinitionCursorAsync(workflow, "Event wait completed.", token);
            return true;
        }
        if (run.Status == TaskRunStatus.Failed)
        {
            await FailWorkflowAsync(workflow, run.FailureReason ?? "Event wait failed.", null, token);
            return true;
        }
        run.ExecutionAttemptId ??= Guid.NewGuid();
        var wait = await store.GetWaitAsync(run.ExecutionAttemptId.Value, token);
        if (wait is null)
        {
            try
            {
                if (!WorkflowWaitRegistry.Default.TryGet(step.Uses, out var definition) || integrations is null)
                    throw new InvalidOperationException("Wait integration is unavailable.");
                var settings = step.Wait ?? throw new InvalidOperationException("Wait settings are missing.");
                var number = settings.IssueNumber;
                CustomTaskInputProvenance? provenance = null;
                if (settings.IssueNumberBinding is { } binding)
                {
                    var document = await ResolveDefinitionAsync(workflow, token);
                    var sources = await ResolveInputProvenanceAsync(workflow, run, step, document, token);
                    provenance = sources["issueNumber"];
                    if (provenance.Value is not { } value || !value.TryGetInt32(out var bound))
                        throw new InvalidOperationException("Issue-number binding must produce a positive integer.");
                    number = bound;
                }
                if (number is not > 0) throw new InvalidOperationException("A positive issue number is required.");
                var correlation = await definition.ResolveAsync(settings, workflow, number.Value, integrations, workItems, token);
                run.Status = TaskRunStatus.Waiting;
                run.StartedAt = clock.UtcNow;
                run.UpdatedAt = clock.UtcNow;
                await store.UpsertTaskRunAsync(run, token);
                wait = await store.ArmWaitAsync(new WorkflowNodeWait
                {
                    WorkflowId = workflow.Id, TaskRunId = run.Id, ExecutionAttemptId = run.ExecutionAttemptId.Value,
                    Uses = step.Uses, Provider = correlation.Provider, RepositoryUrl = correlation.RepositoryUrl,
                    IssueUrl = correlation.IssueUrl, EventSequenceFloor = correlation.EventSequenceFloor, ArmedAt = clock.UtcNow,
                    InputProvenanceJson = provenance is null ? null : JsonSerializer.Serialize(provenance, CustomExecutionJsonOptions)
                }, token);
                await AddEventAsync(workflow.Id, run.Id, "NodeWaiting", "Information", "Waiting for a new issue comment.",
                    new { wait.Id, wait.IssueUrl, wait.ArmedAt, wait.ExecutionAttemptId }, token);
                await TransitionWorkflowAsync(workflow, WorkflowStatus.Running, WorkflowStep.Wait, "Waiting for an event.", token);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or JsonException)
            {
                await CompleteTaskRunAsync(workflow, run, "", false, exception.Message, token);
                await FailWorkflowAsync(workflow, exception.Message, null, token);
                return true;
            }
        }
        var evt = await store.ClaimWaitEventAsync(wait.Id, token);
        if (evt is null) return false;
        // Claim and evidence are durable before task completion/cursor advancement; recovery reuses this claim.
        run.StructuredOutputsJson = evt.OutputsJson;
        await CompleteTaskRunAsync(workflow, run, evt.OutputsJson, true, null, token);
        await AdvanceDefinitionCursorAsync(workflow, "Issue comment received.", token);
        return true;
    }

    private async Task MatchPausedWaitsAsync(Workflow workflow, CancellationToken token)
    {
        foreach (var wait in await store.ListWaitsAsync(workflow.Id, token))
            if (!wait.IsCanceled) await store.ClaimWaitEventAsync(wait.Id, token);
    }
}
