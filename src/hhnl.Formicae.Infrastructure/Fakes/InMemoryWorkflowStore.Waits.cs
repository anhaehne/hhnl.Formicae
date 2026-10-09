using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Infrastructure.Fakes;

public sealed partial class InMemoryWorkflowStore
{
    private readonly Dictionary<Guid, WorkflowNodeWait> waits = [];
    private readonly Dictionary<Guid, WorkflowWaitEvent> waitEvents = [];
    public Task<WorkflowNodeWait> ArmWaitAsync(WorkflowNodeWait wait, CancellationToken token)
    {
        lock (gate) { wait.EventSequenceFloor = Math.Max(wait.EventSequenceFloor, waitEvents.Values
            .Where(evt => evt.Provider == wait.Provider && evt.RepositoryUrl == wait.RepositoryUrl && evt.IssueUrl == wait.IssueUrl && evt.ReceivedAt <= wait.ArmedAt)
            .Select(evt => evt.EventSequence).DefaultIfEmpty().Max()); waits.Add(wait.Id, wait); return Task.FromResult(wait); }
    }
    public Task<WorkflowNodeWait?> GetWaitAsync(Guid attemptId, CancellationToken token)
    {
        lock (gate) return Task.FromResult(waits.Values.SingleOrDefault(wait => wait.ExecutionAttemptId == attemptId));
    }
    public Task<IReadOnlyList<WorkflowNodeWait>> ListWaitsAsync(Guid workflowId, CancellationToken token)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<WorkflowNodeWait>>(waits.Values.Where(wait => wait.WorkflowId == workflowId).ToArray());
    }
    public Task<bool> AcceptWaitEventAsync(WorkflowWaitEvent evt, CancellationToken token)
    {
        lock (gate)
        {
            if (waitEvents.Values.Any(item => item.Provider == evt.Provider && (item.DeliveryId == evt.DeliveryId || item.EventKey == evt.EventKey)))
                return Task.FromResult(false);
            waitEvents.Add(evt.Id, evt); return Task.FromResult(true);
        }
    }
    public Task<WorkflowWaitEvent?> ClaimWaitEventAsync(Guid waitId, CancellationToken token)
    {
        lock (gate)
        {
            var wait = waits[waitId]; var workflow = workflows[wait.WorkflowId]; var run = runs[wait.TaskRunId];
            if (wait.IsCanceled || workflow.CancelRequestedAt is not null || workflow.Status is WorkflowStatus.Canceled or WorkflowStatus.Completed or WorkflowStatus.Failed
                || run.Status != TaskRunStatus.Waiting || run.ExecutionAttemptId != wait.ExecutionAttemptId) return Task.FromResult<WorkflowWaitEvent?>(null);
            if (wait.MatchedEventId is { } matched) return Task.FromResult<WorkflowWaitEvent?>(waitEvents[matched]);
            var candidate = waitEvents.Values.Where(evt => evt.Provider == wait.Provider && evt.Uses == wait.Uses
                && evt.RepositoryUrl == wait.RepositoryUrl && evt.IssueUrl == wait.IssueUrl && evt.CreatedAt >= DateTimeOffset.FromUnixTimeSeconds(wait.ArmedAt.ToUnixTimeSeconds()) && evt.ReceivedAt > wait.ArmedAt && evt.EventSequence > wait.EventSequenceFloor
                && !waits.Values.Any(other => other.WorkflowId == wait.WorkflowId && other.MatchedEventId == evt.Id))
                .OrderBy(evt => evt.ReceivedAt).ThenBy(evt => evt.Id).FirstOrDefault();
            if (candidate is not null) { wait.MatchedEventId = candidate.Id; wait.MatchedAt = DateTimeOffset.UtcNow; }
            return Task.FromResult(candidate);
        }
    }
    public Task CancelWaitsAsync(Guid workflowId, CancellationToken token)
    {
        lock (gate) foreach (var wait in waits.Values.Where(wait => wait.WorkflowId == workflowId)) wait.IsCanceled = true;
        return Task.CompletedTask;
    }
}
