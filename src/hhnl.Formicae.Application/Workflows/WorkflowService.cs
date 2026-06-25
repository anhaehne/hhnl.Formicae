namespace hhnl.Formicae.Application.Workflows;

public sealed class WorkflowService(IWorkflowStore store)
{
    public async Task<WorkflowSummaryResponse> StartGitHubIssueWorkflowAsync(
        StartGitHubIssueWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.IssueUrl))
        {
            throw new ArgumentException("IssueUrl is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.RepositoryUrl))
        {
            throw new ArgumentException("RepositoryUrl is required.", nameof(request));
        }

        var workflow = new Workflow
        {
            IssueUrl = request.IssueUrl,
            RepositoryUrl = request.RepositoryUrl,
            BaseBranch = string.IsNullOrWhiteSpace(request.BaseBranch) ? "main" : request.BaseBranch,
            Model = request.Model,
            Status = WorkflowStatus.Queued,
            CurrentStep = WorkflowStep.None
        };

        await store.CreateWorkflowAsync(workflow, cancellationToken);
        await store.AddLogAsync(new WorkflowLog
        {
            WorkflowId = workflow.Id,
            Message = "Workflow queued from manual GitHub issue trigger."
        }, cancellationToken);

        return workflow.ToSummary();
    }

    public async Task<WorkflowSummaryResponse?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken)
        => (await store.GetWorkflowAsync(workflowId, cancellationToken))?.ToSummary();

    public async Task<WorkflowSummaryResponse[]> ListRecentWorkflowsAsync(int limit, CancellationToken cancellationToken)
        => (await store.ListRecentWorkflowsAsync(limit, cancellationToken))
            .Select(workflow => workflow.ToSummary())
            .ToArray();

    public Task<IReadOnlyList<TaskRun>> ListRunsAsync(Guid workflowId, CancellationToken cancellationToken)
        => store.ListTaskRunsAsync(workflowId, cancellationToken);

    public Task<IReadOnlyList<WorkflowLog>> ListLogsAsync(Guid workflowId, CancellationToken cancellationToken)
        => store.ListLogsAsync(workflowId, cancellationToken);
}
