namespace hhnl.Formicae.Application.Workflows;

public sealed class WorkflowService
{
    private readonly IWorkflowStore store;
    private readonly IWorkItemProvider? workItems;
    private readonly IClock clock;
    private readonly AiSettingsService? aiSettingsService;

    public WorkflowService(IWorkflowStore store, IWorkItemProvider? workItems = null, IClock? clock = null, AiSettingsService? aiSettingsService = null)
    {
        this.store = store;
        this.workItems = workItems;
        this.clock = clock ?? new SystemClock();
        this.aiSettingsService = aiSettingsService;
    }

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

        var model = string.IsNullOrWhiteSpace(request.Model) && aiSettingsService is not null
            ? (await aiSettingsService.ResolveAsync(cancellationToken)).Model
            : request.Model;

        var workflow = new Workflow
        {
            IssueUrl = request.IssueUrl,
            RepositoryUrl = request.RepositoryUrl,
            BaseBranch = string.IsNullOrWhiteSpace(request.BaseBranch) ? "main" : request.BaseBranch,
            Model = model,
            Status = WorkflowStatus.Queued,
            CurrentStep = WorkflowStep.None
        };

        await store.CreateWorkflowAsync(workflow, cancellationToken);
        await store.AddEventAsync(new WorkflowEvent
        {
            WorkflowId = workflow.Id,
            Type = WorkflowEventTypes.WorkflowQueued,
            Message = "Workflow queued from manual GitHub issue trigger.",
            CreatedAt = clock.UtcNow
        }, cancellationToken);
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

    public async Task<TaskRunResponse[]> ListRunsAsync(Guid workflowId, CancellationToken cancellationToken)
        => (await store.ListTaskRunsAsync(workflowId, cancellationToken))
            .Select(run => run.ToResponse())
            .ToArray();

    public async Task<WorkflowEventResponse[]> ListEventsAsync(Guid workflowId, CancellationToken cancellationToken)
        => (await store.ListEventsAsync(workflowId, cancellationToken))
            .Select(evt => evt.ToResponse())
            .ToArray();

    public async Task<WorkflowChatMessageResponse[]> ListChatMessagesAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        if (workItems is null)
        {
            return [];
        }

        var workflow = await store.GetWorkflowAsync(workflowId, cancellationToken);
        if (workflow is null)
        {
            return [];
        }

        var issue = await workItems.GetIssueAsync(workflow.IssueUrl, cancellationToken);
        return issue.UserComments
            .OrderBy(comment => comment.UpdatedAt)
            .Select(comment => new WorkflowChatMessageResponse(comment.Id, comment.Author, comment.Body, comment.Url, comment.UpdatedAt))
            .ToArray();
    }

    public Task<IReadOnlyList<WorkflowLog>> ListLogsAsync(Guid workflowId, CancellationToken cancellationToken)
        => store.ListLogsAsync(workflowId, cancellationToken);
}
