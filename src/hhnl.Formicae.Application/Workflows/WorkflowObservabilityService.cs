using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Application.Workflows;

public sealed class WorkflowObservabilityService(
    IWorkflowStore store,
    IClock clock,
    IOptions<WorkflowObservabilityOptions> options)
{
    public async Task<WorkflowSignalResponse[]> GetWorkflowSignalsAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        var workflow = await store.GetWorkflowAsync(workflowId, cancellationToken);
        if (workflow is null)
        {
            return [];
        }

        var now = clock.UtcNow;
        var settings = options.Value;
        var signals = new List<WorkflowSignalResponse>();
        var runs = await store.ListTaskRunsAsync(workflowId, cancellationToken);

        foreach (var run in runs.Where(run => run.Status == TaskRunStatus.Running))
        {
            if (run.StartedAt is not null && now - run.StartedAt.Value > settings.RunningTaskStuckAfter)
            {
                signals.Add(new WorkflowSignalResponse(
                    "Warning",
                    $"{run.Kind} has been running longer than {settings.RunningTaskStuckAfter}.",
                    workflowId,
                    run.Id,
                    now));
            }

            if (string.IsNullOrWhiteSpace(run.ExternalId))
            {
                signals.Add(new WorkflowSignalResponse(
                    "Error",
                    $"{run.Kind} is running without an external job id.",
                    workflowId,
                    run.Id,
                    now));
            }
        }

        if (workflow.Status is not WorkflowStatus.Completed and not WorkflowStatus.Failed and not WorkflowStatus.Canceled
            && now - workflow.UpdatedAt > settings.WorkflowStaleAfter)
        {
            signals.Add(new WorkflowSignalResponse(
                "Warning",
                $"Workflow has not been updated for longer than {settings.WorkflowStaleAfter}.",
                workflowId,
                null,
                now));
        }

        return signals.ToArray();
    }
}
