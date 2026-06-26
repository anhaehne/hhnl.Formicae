using System.Text.Json;

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

    public async Task<WorkflowSummaryResponse?> RetryTaskRunAsync(Guid workflowId, Guid taskRunId, CancellationToken cancellationToken)
    {
        var workflow = await store.GetWorkflowAsync(workflowId, cancellationToken);
        if (workflow is null)
        {
            return null;
        }

        var runs = await store.ListTaskRunsAsync(workflowId, cancellationToken);
        var run = runs.FirstOrDefault(candidate => candidate.Id == taskRunId);
        if (run is null)
        {
            return null;
        }

        if (run.Status != TaskRunStatus.Failed)
        {
            throw new InvalidOperationException("Only failed task runs can be retried.");
        }

        var retryState = GetRetryWorkflowState(run.Kind);
        var now = clock.UtcNow;

        run.Status = TaskRunStatus.Queued;
        run.ExternalId = null;
        run.Output = null;
        run.FailureReason = null;
        run.StartedAt = null;
        run.CompletedAt = null;
        run.UpdatedAt = now;
        await store.UpsertTaskRunAsync(run, cancellationToken);

        workflow.Status = retryState.Status;
        workflow.CurrentStep = retryState.Step;
        workflow.FailureReason = null;
        workflow.UpdatedAt = now;
        await store.UpdateWorkflowAsync(workflow, cancellationToken);

        var message = $"{run.Kind} task queued for retry.";
        await store.AddEventAsync(new WorkflowEvent
        {
            WorkflowId = workflow.Id,
            TaskRunId = run.Id,
            Type = WorkflowEventTypes.WorkflowTransitioned,
            Message = message,
            DetailsJson = JsonSerializer.Serialize(new
            {
                taskRunId = run.Id,
                taskKind = run.Kind.ToString()
            }),
            CreatedAt = now
        }, cancellationToken);
        await store.AddLogAsync(new WorkflowLog
        {
            WorkflowId = workflow.Id,
            TaskRunId = run.Id,
            Message = message,
            CreatedAt = now
        }, cancellationToken);

        return workflow.ToSummary();
    }

    public async Task<WorkflowSummaryResponse?> RetryWorkflowAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        var workflow = await store.GetWorkflowAsync(workflowId, cancellationToken);
        if (workflow is null)
        {
            return null;
        }

        if (workflow.Status != WorkflowStatus.Failed)
        {
            throw new InvalidOperationException("Only failed workflows can be retried.");
        }

        var runs = await store.ListTaskRunsAsync(workflowId, cancellationToken);
        var failedRun = runs.Reverse().FirstOrDefault(run => run.Status == TaskRunStatus.Failed);
        if (failedRun is not null)
        {
            return await RetryTaskRunAsync(workflowId, failedRun.Id, cancellationToken);
        }

        var retryState = GetRetryWorkflowState(workflow.CurrentStep);
        var now = clock.UtcNow;
        workflow.Status = retryState.Status;
        workflow.CurrentStep = retryState.Step;
        workflow.FailureReason = null;
        workflow.UpdatedAt = now;
        await store.UpdateWorkflowAsync(workflow, cancellationToken);

        var message = retryState.Step == WorkflowStep.None
            ? "Workflow queued for retry."
            : $"{retryState.Step} workflow step queued for retry.";
        await store.AddEventAsync(new WorkflowEvent
        {
            WorkflowId = workflow.Id,
            Type = WorkflowEventTypes.WorkflowTransitioned,
            Message = message,
            DetailsJson = JsonSerializer.Serialize(new
            {
                workflowStep = retryState.Step.ToString()
            }),
            CreatedAt = now
        }, cancellationToken);
        await store.AddLogAsync(new WorkflowLog
        {
            WorkflowId = workflow.Id,
            Message = message,
            CreatedAt = now
        }, cancellationToken);

        return workflow.ToSummary();
    }

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

    private static (WorkflowStatus Status, WorkflowStep Step) GetRetryWorkflowState(TaskRunKind kind)
        => kind switch
        {
            TaskRunKind.Plan => (WorkflowStatus.Planning, WorkflowStep.Plan),
            TaskRunKind.Implement => (WorkflowStatus.Implementing, WorkflowStep.Implement),
            TaskRunKind.CreatePullRequest => (WorkflowStatus.CreatingPullRequest, WorkflowStep.CreatePullRequest),
            TaskRunKind.AddressComments => (WorkflowStatus.Reviewing, WorkflowStep.AddressComments),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported task run kind.")
        };

    private static (WorkflowStatus Status, WorkflowStep Step) GetRetryWorkflowState(WorkflowStep step)
        => step switch
        {
            WorkflowStep.None => (WorkflowStatus.Queued, WorkflowStep.None),
            WorkflowStep.Plan => (WorkflowStatus.Planning, WorkflowStep.Plan),
            WorkflowStep.Implement => (WorkflowStatus.Implementing, WorkflowStep.Implement),
            WorkflowStep.CreatePullRequest => (WorkflowStatus.CreatingPullRequest, WorkflowStep.CreatePullRequest),
            WorkflowStep.AddressComments => (WorkflowStatus.Reviewing, WorkflowStep.AddressComments),
            _ => throw new InvalidOperationException("Completed workflow steps cannot be retried.")
        };
}
