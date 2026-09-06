using hhnl.Formicae.Application.Workflows;
using Microsoft.EntityFrameworkCore;

namespace hhnl.Formicae.Infrastructure.Persistence;

public sealed class EfWorkflowStore(FormicaeDbContext dbContext) : IWorkflowStore
{
    public async Task<Workflow> CreateWorkflowAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync(cancellationToken);
        return workflow;
    }

    public Task<Workflow?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken)
        => dbContext.Workflows.SingleOrDefaultAsync(workflow => workflow.Id == workflowId, cancellationToken);

    public Task<Workflow?> GetWorkflowByIssueUrlAsync(string issueUrl, CancellationToken cancellationToken)
        => dbContext.Workflows.SingleOrDefaultAsync(workflow => workflow.IssueUrl == issueUrl, cancellationToken);

    public async Task<IReadOnlyList<Workflow>> ListRecentWorkflowsAsync(int limit, CancellationToken cancellationToken)
        => await dbContext.Workflows
            .AsNoTracking()
            .OrderByDescending(workflow => workflow.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public Task<Workflow?> GetWorkflowByPullRequestUrlAsync(string pullRequestUrl, CancellationToken cancellationToken)
        => dbContext.Workflows.SingleOrDefaultAsync(workflow => workflow.PullRequestUrl == pullRequestUrl, cancellationToken);

    public async Task<IReadOnlyList<Workflow>> ListRunnableWorkflowsAsync(CancellationToken cancellationToken)
        => await dbContext.Workflows
            .Where(workflow => workflow.Status == WorkflowStatus.Queued
                || workflow.Status == WorkflowStatus.Planning
                || workflow.Status == WorkflowStatus.Implementing
                || workflow.Status == WorkflowStatus.CreatingPullRequest
                || workflow.Status == WorkflowStatus.Reviewing
                || workflow.Status == WorkflowStatus.Running)
            .OrderBy(workflow => workflow.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Workflow>> ListNonTerminalWorkflowsAsync(CancellationToken cancellationToken)
        => await dbContext.Workflows
            .Where(workflow => workflow.Status != WorkflowStatus.Completed
                && workflow.Status != WorkflowStatus.Failed
                && workflow.Status != WorkflowStatus.Canceled)
            .OrderBy(workflow => workflow.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task UpdateWorkflowAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        dbContext.Workflows.Update(workflow);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<TaskRun> UpsertTaskRunAsync(TaskRun taskRun, CancellationToken cancellationToken)
    {
        var exists = await dbContext.TaskRuns.AnyAsync(run => run.Id == taskRun.Id, cancellationToken);
        if (exists)
        {
            dbContext.TaskRuns.Update(taskRun);
        }
        else
        {
            dbContext.TaskRuns.Add(taskRun);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return taskRun;
    }

    public Task<TaskRun?> GetTaskRunAsync(Guid workflowId, TaskRunKind kind, CancellationToken cancellationToken)
        => dbContext.TaskRuns.Where(run => run.WorkflowId == workflowId && run.Kind == kind)
            .OrderByDescending(run => run.CreatedAt).FirstOrDefaultAsync(cancellationToken);

    public Task<TaskRun?> GetTaskRunExecutionAsync(Guid workflowId, string definitionStepId, int? loopIteration, CancellationToken cancellationToken)
        => dbContext.TaskRuns.SingleOrDefaultAsync(run => run.WorkflowId == workflowId
            && run.DefinitionStepId == definitionStepId && run.LoopIteration == loopIteration, cancellationToken);

    public async Task<IReadOnlyList<TaskRun>> ListTaskRunsAsync(Guid workflowId, CancellationToken cancellationToken)
        => await dbContext.TaskRuns.Where(run => run.WorkflowId == workflowId).OrderBy(run => run.CreatedAt).ToListAsync(cancellationToken);

    public async Task<WorkflowLoopIteration> UpsertLoopIterationAsync(WorkflowLoopIteration iteration, CancellationToken cancellationToken)
    {
        if (await dbContext.WorkflowLoopIterations.AnyAsync(item => item.Id == iteration.Id, cancellationToken))
            dbContext.WorkflowLoopIterations.Update(iteration);
        else
            dbContext.WorkflowLoopIterations.Add(iteration);
        await dbContext.SaveChangesAsync(cancellationToken);
        return iteration;
    }

    public async Task<IReadOnlyList<WorkflowLoopIteration>> ListLoopIterationsAsync(Guid workflowId, CancellationToken cancellationToken)
        => await dbContext.WorkflowLoopIterations.Where(item => item.WorkflowId == workflowId)
            .OrderBy(item => item.StartedAt).ThenBy(item => item.IterationNumber).ToListAsync(cancellationToken);

    public Task<WorkflowParallelExecution?> GetParallelExecutionAsync(Guid workflowId, string nodeId, CancellationToken cancellationToken)
        => dbContext.WorkflowParallelExecutions.SingleOrDefaultAsync(execution => execution.WorkflowId == workflowId
            && execution.NodeId == nodeId, cancellationToken);

    public async Task<WorkflowParallelExecution> UpsertParallelExecutionAsync(WorkflowParallelExecution execution, CancellationToken cancellationToken)
    {
        if (await dbContext.WorkflowParallelExecutions.AnyAsync(item => item.Id == execution.Id, cancellationToken))
            dbContext.WorkflowParallelExecutions.Update(execution);
        else
            dbContext.WorkflowParallelExecutions.Add(execution);
        await dbContext.SaveChangesAsync(cancellationToken);
        return execution;
    }

    public Task<WorkflowDecisionExecution?> GetDecisionExecutionAsync(Guid workflowId, string nodeId, CancellationToken cancellationToken)
        => dbContext.WorkflowDecisionExecutions.AsNoTracking().SingleOrDefaultAsync(execution => execution.WorkflowId == workflowId
            && execution.NodeId == nodeId, cancellationToken);

    public async Task<IReadOnlyList<WorkflowDecisionExecution>> ListDecisionExecutionsAsync(Guid workflowId, CancellationToken cancellationToken)
        => await dbContext.WorkflowDecisionExecutions.AsNoTracking().Where(execution => execution.WorkflowId == workflowId)
            .OrderBy(execution => execution.EvaluatedAt).ThenBy(execution => execution.Id).ToListAsync(cancellationToken);

    public async Task<WorkflowDecisionCommitResult> CommitDecisionAsync(WorkflowDecisionExecution proposed, WorkflowStatus nextStatus,
        WorkflowStep nextStep, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        // Serialize outcome insertion and cursor advancement against the current persisted workflow row.
        var workflow = await dbContext.Workflows.FromSqlInterpolated($"SELECT * FROM workflows WHERE \"Id\" = {proposed.WorkflowId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The decision workflow does not exist.");
        var existing = await GetDecisionExecutionAsync(proposed.WorkflowId, proposed.NodeId, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(existing, workflow, false);
        }
        if (workflow.CurrentDefinitionStepId != proposed.NodeId
            || workflow.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Canceled)
            throw new InvalidOperationException("The workflow is no longer awaiting this decision.");
        try
        {
            dbContext.WorkflowDecisionExecutions.Add(proposed);
            await dbContext.SaveChangesAsync(cancellationToken);
            await dbContext.Workflows.Where(item => item.Id == workflow.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.CurrentDefinitionStepId, proposed.SelectedTargetId)
                .SetProperty(item => item.Status, nextStatus)
                .SetProperty(item => item.CurrentStep, nextStep)
                .SetProperty(item => item.FailureReason, (string?)null)
                .SetProperty(item => item.UpdatedAt, proposed.EvaluatedAt), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            // A failed/uncertain transaction must never leave its pending insert for a later logging SaveChanges.
            dbContext.Entry(proposed).State = EntityState.Detached;
        }
        workflow.CurrentDefinitionStepId = proposed.SelectedTargetId;
        workflow.Status = nextStatus; workflow.CurrentStep = nextStep;
        workflow.FailureReason = null; workflow.UpdatedAt = proposed.EvaluatedAt;
        return new(proposed, workflow, true);
    }

    public async Task AddEventAsync(WorkflowEvent evt, CancellationToken cancellationToken)
    {
        dbContext.WorkflowEvents.Add(evt);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowEvent>> ListEventsAsync(Guid workflowId, CancellationToken cancellationToken)
        => await dbContext.WorkflowEvents
            .Where(evt => evt.WorkflowId == workflowId)
            .OrderByDescending(evt => evt.CreatedAt)
            .ThenByDescending(evt => evt.Id)
            .ToListAsync(cancellationToken);

    public async Task AddTriggerEventAsync(WorkflowTriggerEvent evt, CancellationToken cancellationToken)
    {
        dbContext.WorkflowTriggerEvents.Add(evt);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowTriggerEvent>> ListTriggerEventsAsync(Guid workflowId, CancellationToken cancellationToken)
        => await dbContext.WorkflowTriggerEvents
            .AsNoTracking()
            .Where(evt => evt.WorkflowId == workflowId)
            .OrderByDescending(evt => evt.CreatedAt)
            .ThenByDescending(evt => evt.Id)
            .ToListAsync(cancellationToken);

    public Task<WorkflowTriggerEvent?> GetTriggerEventByDeliveryAsync(string deliveryId, string triggerId, CancellationToken cancellationToken)
        => dbContext.WorkflowTriggerEvents
            .AsNoTracking()
            .SingleOrDefaultAsync(evt => evt.ExternalDeliveryId == deliveryId && evt.TriggerId == triggerId, cancellationToken);

    public async Task AddLogAsync(WorkflowLog log, CancellationToken cancellationToken)
    {
        dbContext.WorkflowLogs.Add(log);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowLog>> ListLogsAsync(Guid workflowId, CancellationToken cancellationToken)
        => await dbContext.WorkflowLogs.Where(log => log.WorkflowId == workflowId).OrderBy(log => log.CreatedAt).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<WorkflowDefinition>> ListWorkflowDefinitionsAsync(CancellationToken cancellationToken)
        => await dbContext.WorkflowDefinitions
            .AsNoTracking()
            .OrderBy(definition => definition.Name)
            .ToListAsync(cancellationToken);

    public Task<WorkflowDefinition?> GetWorkflowDefinitionAsync(Guid definitionId, CancellationToken cancellationToken)
        => dbContext.WorkflowDefinitions.SingleOrDefaultAsync(definition => definition.Id == definitionId, cancellationToken);

    public async Task<WorkflowDefinition> CreateWorkflowDefinitionAsync(WorkflowDefinition definition, CancellationToken cancellationToken)
    {
        dbContext.WorkflowDefinitions.Add(definition);
        await dbContext.SaveChangesAsync(cancellationToken);
        return definition;
    }

    public async Task<IReadOnlyList<WorkflowDefinitionVersion>> ListWorkflowDefinitionVersionsAsync(Guid definitionId, CancellationToken cancellationToken)
        => await dbContext.WorkflowDefinitionVersions
            .AsNoTracking()
            .Where(version => version.WorkflowDefinitionId == definitionId)
            .OrderByDescending(version => version.Version)
            .ToListAsync(cancellationToken);

    public Task<WorkflowDefinitionVersion?> GetWorkflowDefinitionVersionAsync(Guid versionId, CancellationToken cancellationToken)
        => dbContext.WorkflowDefinitionVersions.SingleOrDefaultAsync(version => version.Id == versionId, cancellationToken);

    public Task<WorkflowDefinitionVersion?> GetLatestWorkflowDefinitionVersionAsync(Guid definitionId, CancellationToken cancellationToken)
        => dbContext.WorkflowDefinitionVersions
            .Where(version => version.WorkflowDefinitionId == definitionId)
            .OrderByDescending(version => version.Version)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<WorkflowDefinitionVersion?> GetLatestEnabledWorkflowDefinitionVersionAsync(Guid definitionId, CancellationToken cancellationToken)
        => dbContext.WorkflowDefinitionVersions
            .Where(version => version.WorkflowDefinitionId == definitionId && version.IsEnabled)
            .OrderByDescending(version => version.Version)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<WorkflowDefinitionVersion?> GetDefaultEnabledWorkflowDefinitionVersionAsync(CancellationToken cancellationToken)
        => dbContext.WorkflowDefinitionVersions
            .Where(version => version.IsDefault && version.IsEnabled)
            .OrderByDescending(version => version.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<WorkflowDefinitionVersion> CreateWorkflowDefinitionVersionAsync(WorkflowDefinitionVersion version, CancellationToken cancellationToken)
    {
        if (version.IsDefault)
        {
            await dbContext.WorkflowDefinitionVersions
                .Where(current => current.IsDefault)
                .ExecuteUpdateAsync(setters => setters.SetProperty(current => current.IsDefault, false), cancellationToken);
        }

        dbContext.WorkflowDefinitionVersions.Add(version);
        await dbContext.SaveChangesAsync(cancellationToken);
        return version;
    }

    public async Task EnsureDefaultWorkflowDefinitionAsync(
        WorkflowDefinition definition,
        WorkflowDefinitionVersion version,
        CancellationToken cancellationToken)
    {
        if (await dbContext.WorkflowDefinitionVersions.AnyAsync(candidate => candidate.IsDefault && candidate.IsEnabled, cancellationToken))
        {
            return;
        }

        if (!await dbContext.WorkflowDefinitions.AnyAsync(candidate => candidate.Id == definition.Id, cancellationToken))
        {
            dbContext.WorkflowDefinitions.Add(definition);
        }

        if (!await dbContext.WorkflowDefinitionVersions.AnyAsync(candidate => candidate.Id == version.Id, cancellationToken))
        {
            dbContext.WorkflowDefinitionVersions.Add(version);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
