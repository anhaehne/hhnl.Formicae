using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Infrastructure.Fakes;

public sealed partial class InMemoryWorkflowStore
{
    private readonly List<TaskRunAttempt> attempts = [];
    private long logSequence;
    private bool AppendLogLocked(WorkflowLog log)
    {
        if (logs.Any(item => item.Id == log.Id || (log.SourceSequence is not null && log.ExecutionAttemptId is not null
            && item.WorkflowId == log.WorkflowId && item.ExecutionAttemptId == log.ExecutionAttemptId
            && item.Source == log.Source && item.SourceSequence == log.SourceSequence))) return false;
        log.Sequence = ++logSequence;
        logs.Add(log);
        return true;
    }
    public Task<IReadOnlyList<WorkflowLog>> AppendLogsAsync(IReadOnlyList<WorkflowLog> items, CancellationToken token)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<WorkflowLog>>(items.Where(AppendLogLocked).ToArray());
    }
    public Task<bool> TryAddWorkerLogAsync(WorkflowLog log, string externalId, Guid? expectedAttempt, CancellationToken token)
    {
        lock (gate)
        {
            if (logs.Any(item => item.Id == log.Id && item.WorkflowId == log.WorkflowId && item.TaskRunId == log.TaskRunId
                && item.ExecutionAttemptId == log.ExecutionAttemptId && item.ExternalId == externalId
                && (expectedAttempt is null || item.ExecutionAttemptId == expectedAttempt))) return Task.FromResult(true);
            var run = runs.Values.SingleOrDefault(item => item.WorkflowId == log.WorkflowId && item.Id == log.TaskRunId);
            if (run is null || run.Status != TaskRunStatus.Running || (expectedAttempt is not null && run.ExecutionAttemptId != expectedAttempt)
                || (log.ExecutionAttemptId is not null && run.ExecutionAttemptId != log.ExecutionAttemptId)
                || (run.ExternalId != externalId && !(run.ExternalId is null && expectedAttempt is not null && run.ExecutionAttemptId == expectedAttempt)))
                return Task.FromResult(false);
            if (!workflows.TryGetValue(log.WorkflowId, out var workflow) || workflow.CancelCompletedAt is not null)
                return Task.FromResult(false);
            AppendLogLocked(log);
            return Task.FromResult(true);
        }
    }
    public Task<WorkflowLogPage> QueryLogsAsync(Guid id, WorkflowLogQuery query, CancellationToken token)
    {
        query = WorkflowExecutionQueries.Validate(query);
        lock (gate)
        {
            var all = WorkflowExecutionQueries.Filter(logs.AsQueryable(), id, query);
            var selected = query.After is { } after ? all.Where(log => log.Sequence > after).OrderBy(log => log.Sequence)
                : (query.Before is { } before ? all.Where(log => log.Sequence < before) : all).OrderByDescending(log => log.Sequence);
            var items = selected.Take(query.Limit).OrderBy(log => log.Sequence).ToArray();
            var first = items.FirstOrDefault()?.Sequence ?? 0;
            var last = items.LastOrDefault()?.Sequence ?? query.After ?? 0;
            return Task.FromResult(new WorkflowLogPage(items, last, first,
                items.Length > 0 && all.Any(log => log.Sequence > last), items.Length > 0 && all.Any(log => log.Sequence < first)));
        }
    }
    public Task<WorkflowSearchPage> SearchWorkflowsAsync(WorkflowSearchQuery query, CancellationToken token)
    {
        query = WorkflowExecutionQueries.Validate(query);
        lock (gate)
        {
            var all = WorkflowExecutionQueries.Filter(workflows.Values.AsQueryable(), query);
            return Task.FromResult(new WorkflowSearchPage(all.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
                .Skip(query.Offset).Take(query.Limit).Select(item => item.ToSummary()).ToArray(), all.Count(), query.Offset, query.Limit));
        }
    }
    public Task ArchiveTaskRunAttemptAsync(TaskRun run, CancellationToken token)
    {
        lock (gate)
        {
            var identity = run.ExecutionAttemptId ?? run.Id;
            if (!attempts.Any(item => item.TaskRunId == run.Id && item.ExecutionAttemptId == identity))
                attempts.Add(TaskRunAttempt.Snapshot(run, 1 + attempts.Count(item => item.TaskRunId == run.Id)));
        }
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<TaskRunAttempt>> ListTaskRunAttemptsAsync(Guid id, CancellationToken token)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<TaskRunAttempt>>(attempts.Where(item => item.WorkflowId == id)
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.AttemptNumber).ToArray());
    }
}
