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

    public async Task AddLogAsync(WorkflowLog log, CancellationToken cancellationToken)
    {
        dbContext.WorkflowLogs.Add(log);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowLog>> ListLogsAsync(Guid workflowId, CancellationToken cancellationToken)
        => await dbContext.WorkflowLogs.Where(log => log.WorkflowId == workflowId).OrderBy(log => log.CreatedAt).ToListAsync(cancellationToken);
}
