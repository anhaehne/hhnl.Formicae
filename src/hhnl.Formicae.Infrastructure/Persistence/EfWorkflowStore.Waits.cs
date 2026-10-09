using hhnl.Formicae.Application.Workflows;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace hhnl.Formicae.Infrastructure.Persistence;

public sealed partial class EfWorkflowStore
{
    public async Task<WorkflowNodeWait> ArmWaitAsync(WorkflowNodeWait wait, CancellationToken token)
    {
        wait.EventSequenceFloor = Math.Max(wait.EventSequenceFloor, await dbContext.WorkflowWaitEvents
            .Where(evt => evt.Provider == wait.Provider && evt.RepositoryUrl == wait.RepositoryUrl && evt.IssueUrl == wait.IssueUrl && evt.ReceivedAt <= wait.ArmedAt)
            .Select(evt => (long?)evt.EventSequence).MaxAsync(token) ?? 0);
        dbContext.WorkflowNodeWaits.Add(wait);
        await dbContext.SaveChangesAsync(token);
        return wait;
    }
    public Task<WorkflowNodeWait?> GetWaitAsync(Guid attemptId, CancellationToken token)
        => dbContext.WorkflowNodeWaits.AsNoTracking().SingleOrDefaultAsync(wait => wait.ExecutionAttemptId == attemptId, token);
    public async Task<IReadOnlyList<WorkflowNodeWait>> ListWaitsAsync(Guid workflowId, CancellationToken token)
        => await dbContext.WorkflowNodeWaits.AsNoTracking().Where(wait => wait.WorkflowId == workflowId).ToArrayAsync(token);
    public async Task<bool> AcceptWaitEventAsync(WorkflowWaitEvent evt, CancellationToken token)
    {
        dbContext.WorkflowWaitEvents.Add(evt);
        try { await dbContext.SaveChangesAsync(token); return true; }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            dbContext.Entry(evt).State = EntityState.Detached;
            return false;
        }
    }
    public async Task<WorkflowWaitEvent?> ClaimWaitEventAsync(Guid waitId, CancellationToken token)
    {
        var wait = await dbContext.WorkflowNodeWaits.AsNoTracking().SingleAsync(wait => wait.Id == waitId, token);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(token);
        // Serialize claims and control operations for this execution, in addition to the scheduler's distributed lock.
        var workflow = await dbContext.Workflows.FromSqlInterpolated($"SELECT * FROM workflows WHERE \"Id\" = {wait.WorkflowId} FOR UPDATE")
            .AsNoTracking().SingleAsync(token);
        wait = await dbContext.WorkflowNodeWaits.AsNoTracking().SingleAsync(item => item.Id == waitId, token);
        if (wait.IsCanceled || workflow.CancelRequestedAt is not null || workflow.Status is WorkflowStatus.Canceled or WorkflowStatus.Completed or WorkflowStatus.Failed)
            return null;
        if (!await dbContext.TaskRuns.AnyAsync(run => run.Id == wait.TaskRunId && run.ExecutionAttemptId == wait.ExecutionAttemptId
            && run.Status == TaskRunStatus.Waiting, token)) return null;
        if (wait.MatchedEventId is { } accepted)
            return await dbContext.WorkflowWaitEvents.AsNoTracking().SingleAsync(evt => evt.Id == accepted, token);
        var createdBoundary = DateTimeOffset.FromUnixTimeSeconds(wait.ArmedAt.ToUnixTimeSeconds());
        var candidate = await dbContext.WorkflowWaitEvents.AsNoTracking()
            .Where(evt => evt.Provider == wait.Provider && evt.Uses == wait.Uses && evt.RepositoryUrl == wait.RepositoryUrl && evt.IssueUrl == wait.IssueUrl
                && evt.CreatedAt >= createdBoundary && evt.ReceivedAt > wait.ArmedAt && evt.EventSequence > wait.EventSequenceFloor
                && !dbContext.WorkflowNodeWaits.Any(other => other.WorkflowId == wait.WorkflowId && other.MatchedEventId == evt.Id))
            .OrderBy(evt => evt.ReceivedAt).ThenBy(evt => evt.Id).FirstOrDefaultAsync(token);
        if (candidate is null) return null;
        var changed = await dbContext.WorkflowNodeWaits.Where(item => item.Id == wait.Id && item.MatchedEventId == null && !item.IsCanceled)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.MatchedEventId, candidate.Id)
                .SetProperty(item => item.MatchedAt, DateTimeOffset.UtcNow), token);
        await transaction.CommitAsync(token);
        return changed == 1 ? candidate : null;
    }
    public async Task CancelWaitsAsync(Guid workflowId, CancellationToken token)
        => await dbContext.WorkflowNodeWaits.Where(wait => wait.WorkflowId == workflowId && !wait.IsCanceled)
            .ExecuteUpdateAsync(setters => setters.SetProperty(wait => wait.IsCanceled, true), token);
}
