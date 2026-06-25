using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Infrastructure.Fakes;

public sealed class InMemoryWorkflowStore : IWorkflowStore
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Workflow> workflows = [];
    private readonly Dictionary<Guid, TaskRun> runs = [];
    private readonly List<WorkflowLog> logs = [];

    public Task<Workflow> CreateWorkflowAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            workflows.Add(workflow.Id, workflow);
        }

        return Task.FromResult(workflow);
    }

    public Task<Workflow?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(workflows.GetValueOrDefault(workflowId));
        }
    }

    public Task<Workflow?> GetWorkflowByIssueUrlAsync(string issueUrl, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(workflows.Values.SingleOrDefault(workflow => string.Equals(workflow.IssueUrl, issueUrl, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public Task<IReadOnlyList<Workflow>> ListRecentWorkflowsAsync(int limit, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<Workflow>>(workflows.Values
                .OrderByDescending(workflow => workflow.CreatedAt)
                .Take(limit)
                .ToArray());
        }
    }

    public Task<Workflow?> GetWorkflowByPullRequestUrlAsync(string pullRequestUrl, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(workflows.Values.SingleOrDefault(workflow => string.Equals(workflow.PullRequestUrl, pullRequestUrl, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public Task<IReadOnlyList<Workflow>> ListRunnableWorkflowsAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<Workflow>>(workflows.Values
                .Where(workflow => workflow.Status is WorkflowStatus.Queued or WorkflowStatus.Planning or WorkflowStatus.Implementing or WorkflowStatus.CreatingPullRequest or WorkflowStatus.Reviewing)
                .OrderBy(workflow => workflow.CreatedAt)
                .ToArray());
        }
    }

    public Task UpdateWorkflowAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            workflows[workflow.Id] = workflow;
        }

        return Task.CompletedTask;
    }

    public Task<TaskRun> UpsertTaskRunAsync(TaskRun taskRun, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            runs[taskRun.Id] = taskRun;
        }

        return Task.FromResult(taskRun);
    }

    public Task<TaskRun?> GetTaskRunAsync(Guid workflowId, TaskRunKind kind, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(runs.Values.SingleOrDefault(run => run.WorkflowId == workflowId && run.Kind == kind));
        }
    }

    public Task<IReadOnlyList<TaskRun>> ListTaskRunsAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<TaskRun>>(runs.Values
                .Where(run => run.WorkflowId == workflowId)
                .OrderBy(run => run.CreatedAt)
                .ToArray());
        }
    }

    public Task AddLogAsync(WorkflowLog log, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            logs.Add(log);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkflowLog>> ListLogsAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<WorkflowLog>>(logs
                .Where(log => log.WorkflowId == workflowId)
                .OrderBy(log => log.CreatedAt)
                .ToArray());
        }
    }
}
