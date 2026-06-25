namespace hhnl.Formicae.Application.Workflows;

public interface IWorkItemProvider
{
    Task<WorkItem> GetIssueAsync(string issueUrl, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkItem>> ListIssuesWithLabelAsync(
        string repositoryUrl,
        string label,
        CancellationToken cancellationToken);

    Task UpsertIssueCommentAsync(
        string issueUrl,
        string marker,
        string body,
        CancellationToken cancellationToken);
}

public interface ISourceControlProvider
{
    Task<string> CreateBranchAsync(string repositoryUrl, string baseBranch, Guid workflowId, CancellationToken cancellationToken);

    Task<PullRequestResult> CreateDraftPullRequestAsync(
        Workflow workflow,
        IReadOnlyList<TaskRun> taskRuns,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PullRequestComment>> ListPullRequestCommentsAsync(
        Workflow workflow,
        CancellationToken cancellationToken);

    Task UpsertPullRequestCommentAsync(
        Workflow workflow,
        string body,
        CancellationToken cancellationToken);
}

public interface IAgentRunner
{
    Task<AgentRunResult> RunAsync(AgentTask task, CancellationToken cancellationToken);
}

public interface IWorkflowStore
{
    Task<Workflow> CreateWorkflowAsync(Workflow workflow, CancellationToken cancellationToken);
    Task<Workflow?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken);
    Task<Workflow?> GetWorkflowByIssueUrlAsync(string issueUrl, CancellationToken cancellationToken);
    Task<IReadOnlyList<Workflow>> ListRunnableWorkflowsAsync(CancellationToken cancellationToken);
    Task UpdateWorkflowAsync(Workflow workflow, CancellationToken cancellationToken);
    Task<TaskRun> UpsertTaskRunAsync(TaskRun taskRun, CancellationToken cancellationToken);
    Task<TaskRun?> GetTaskRunAsync(Guid workflowId, TaskRunKind kind, CancellationToken cancellationToken);
    Task<IReadOnlyList<TaskRun>> ListTaskRunsAsync(Guid workflowId, CancellationToken cancellationToken);
    Task AddLogAsync(WorkflowLog log, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkflowLog>> ListLogsAsync(Guid workflowId, CancellationToken cancellationToken);
}

public interface IPromptRenderer
{
    Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? workItem, CancellationToken cancellationToken);

    Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? workItem, IReadOnlyList<PullRequestComment> pullRequestComments, CancellationToken cancellationToken);
}
