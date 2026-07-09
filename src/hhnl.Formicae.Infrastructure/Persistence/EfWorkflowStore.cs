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
                || workflow.Status == WorkflowStatus.Reviewing)
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
        => dbContext.TaskRuns.SingleOrDefaultAsync(run => run.WorkflowId == workflowId && run.Kind == kind, cancellationToken);

    public async Task<IReadOnlyList<TaskRun>> ListTaskRunsAsync(Guid workflowId, CancellationToken cancellationToken)
        => await dbContext.TaskRuns.Where(run => run.WorkflowId == workflowId).OrderBy(run => run.CreatedAt).ToListAsync(cancellationToken);

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
