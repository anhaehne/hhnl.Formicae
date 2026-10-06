using hhnl.Formicae.Application.Workflows;
using Microsoft.EntityFrameworkCore;

namespace hhnl.Formicae.Infrastructure.Persistence;

public sealed partial class EfWorkflowStore
{
    // The row lock is acquired before PostgreSQL allocates any identity. Thus a stream cursor
    // can never advance past a lower, uncommitted identity belonging to this workflow.
    private async Task LockLogWorkflowAsync(Guid id, CancellationToken token)
        => _ = await dbContext.Workflows.FromSqlInterpolated($"SELECT * FROM workflows WHERE \"Id\" = {id} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(token) ?? throw new InvalidOperationException("Workflow does not exist.");

    public async Task<IReadOnlyList<WorkflowLog>> AppendLogsAsync(IReadOnlyList<WorkflowLog> logs, CancellationToken token)
    {
        if (logs.Count == 0) return [];
        if (logs.Select(item => item.WorkflowId).Distinct().Count() != 1)
            throw new ArgumentException("A log batch must belong to one workflow.");
        await using var transaction = await dbContext.Database.BeginTransactionAsync(token);
        await LockLogWorkflowAsync(logs[0].WorkflowId, token);
        var inserted = await InsertLogsLockedAsync(logs, token);
        await transaction.CommitAsync(token);
        return inserted;
    }
    private async Task<IReadOnlyList<WorkflowLog>> InsertLogsLockedAsync(IReadOnlyList<WorkflowLog> logs, CancellationToken token)
    {
        var inserted = new List<WorkflowLog>();
        foreach (var log in logs)
        {
            if (inserted.Any(item => item.Id == log.Id || (log.SourceSequence != null && log.ExecutionAttemptId != null
                && item.ExecutionAttemptId == log.ExecutionAttemptId && item.Source == log.Source && item.SourceSequence == log.SourceSequence))
                || await dbContext.WorkflowLogs.AnyAsync(item => item.Id == log.Id
                || (log.SourceSequence != null && log.ExecutionAttemptId != null && item.WorkflowId == log.WorkflowId
                    && item.ExecutionAttemptId == log.ExecutionAttemptId && item.Source == log.Source && item.SourceSequence == log.SourceSequence), token)) continue;
            log.Sequence = 0;
            dbContext.WorkflowLogs.Add(log);
            inserted.Add(log);
        }
        await dbContext.SaveChangesAsync(token);
        return inserted;
    }
    public async Task<bool> TryAddWorkerLogAsync(WorkflowLog log, string externalId, Guid? expectedAttempt, CancellationToken token)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(token);
        await LockLogWorkflowAsync(log.WorkflowId, token);
        if (await dbContext.WorkflowLogs.AnyAsync(item => item.Id == log.Id && item.WorkflowId == log.WorkflowId
            && item.TaskRunId == log.TaskRunId && item.ExecutionAttemptId == log.ExecutionAttemptId
            && item.ExternalId == externalId && (expectedAttempt == null || item.ExecutionAttemptId == expectedAttempt), token))
        {
            await transaction.CommitAsync(token);
            return true;
        }
        var run = await dbContext.TaskRuns.AsNoTracking().SingleOrDefaultAsync(item => item.WorkflowId == log.WorkflowId && item.Id == log.TaskRunId, token);
        var workflow = await dbContext.Workflows.AsNoTracking().SingleAsync(item => item.Id == log.WorkflowId, token);
        if (run is null || run.Status != TaskRunStatus.Running || workflow.CancelCompletedAt is not null
            || (log.ExecutionAttemptId != null && run.ExecutionAttemptId != log.ExecutionAttemptId)
            || (expectedAttempt is not null && run.ExecutionAttemptId != expectedAttempt)
            || (run.ExternalId != externalId && !(run.ExternalId is null && expectedAttempt is not null && run.ExecutionAttemptId == expectedAttempt))) return false;
        await InsertLogsLockedAsync([log], token);
        await transaction.CommitAsync(token);
        return true;
    }
    public async Task<WorkflowLogPage> QueryLogsAsync(Guid id, WorkflowLogQuery query, CancellationToken token)
    {
        query = WorkflowExecutionQueries.Validate(query);
        var all = WorkflowExecutionQueries.Filter(dbContext.WorkflowLogs.AsNoTracking(), id, query);
        var selected = query.After is { } after ? all.Where(log => log.Sequence > after).OrderBy(log => log.Sequence)
            : (query.Before is { } before ? all.Where(log => log.Sequence < before) : all).OrderByDescending(log => log.Sequence);
        var rows = await selected.Take(query.Limit).ToArrayAsync(token);
        var items = rows.OrderBy(log => log.Sequence).ToArray();
        var first = items.FirstOrDefault()?.Sequence ?? 0;
        var last = items.LastOrDefault()?.Sequence ?? query.After ?? 0;
        return new(items, last, first, items.Length > 0 && await all.AnyAsync(log => log.Sequence > last, token),
            items.Length > 0 && await all.AnyAsync(log => log.Sequence < first, token));
    }
    public async Task<WorkflowSearchPage> SearchWorkflowsAsync(WorkflowSearchQuery query, CancellationToken token)
    {
        query = WorkflowExecutionQueries.Validate(query);
        var all = WorkflowExecutionQueries.Filter(dbContext.Workflows.AsNoTracking(), query);
        var total = await all.CountAsync(token);
        var items = await all.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            .Skip(query.Offset).Take(query.Limit).ToArrayAsync(token);
        return new(items.Select(item => item.ToSummary()).ToArray(), total, query.Offset, query.Limit);
    }
    public async Task ArchiveTaskRunAttemptAsync(TaskRun run, CancellationToken token)
    {
        var identity = run.ExecutionAttemptId ?? run.Id;
        if (await dbContext.TaskRunAttempts.AnyAsync(item => item.TaskRunId == run.Id && item.ExecutionAttemptId == identity, token)) return;
        var number = 1 + await dbContext.TaskRunAttempts.CountAsync(item => item.TaskRunId == run.Id, token);
        dbContext.TaskRunAttempts.Add(TaskRunAttempt.Snapshot(run, number));
        await dbContext.SaveChangesAsync(token);
    }
    public async Task<IReadOnlyList<TaskRunAttempt>> ListTaskRunAttemptsAsync(Guid id, CancellationToken token)
        => await dbContext.TaskRunAttempts.AsNoTracking().Where(item => item.WorkflowId == id)
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.AttemptNumber).ToArrayAsync(token);
}
