using System.Text;
using System.Text.Json;

namespace hhnl.Formicae.Application.Workflows;

public sealed class WorkflowOrchestrator(
    IWorkflowStore store,
    IWorkItemProvider workItems,
    ISourceControlProvider sourceControl,
    IAgentRunner agentRunner,
    IPromptRenderer promptRenderer,
    IClock? clock = null)
{
    private readonly IClock clock = clock ?? new SystemClock();

    public async Task<int> AdvanceRunnableWorkflowsAsync(CancellationToken cancellationToken)
    {
        var workflows = await store.ListRunnableWorkflowsAsync(cancellationToken);
        var advanced = 0;

        foreach (var workflow in workflows)
        {
            if (await AdvanceAsync(workflow, cancellationToken))
            {
                advanced++;
            }
        }

        return advanced;
    }

    public async Task<bool> AdvanceAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        if (workflow.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Canceled)
        {
            return false;
        }

        try
        {
            switch (workflow.Status)
            {
                case WorkflowStatus.Queued:
                    return await StartPlanningIfReadyAsync(workflow, cancellationToken);
                case WorkflowStatus.Planning:
                    return await RunPlanningAsync(workflow, null, cancellationToken);
                case WorkflowStatus.Implementing:
                    return await RunImplementationIfReadyAsync(workflow, cancellationToken);
                case WorkflowStatus.CreatingPullRequest:
                    return await CreatePullRequestAsync(workflow, cancellationToken);
                case WorkflowStatus.Reviewing:
                    return await AddressPullRequestCommentsAsync(workflow, cancellationToken);
            }
        }
        catch (WorkItemProviderUnavailableException exception)
        {
            await store.AddLogAsync(new WorkflowLog
            {
                WorkflowId = workflow.Id,
                Level = "Warning",
                Message = $"Work item provider is temporarily unavailable: {exception.Message}",
                CreatedAt = clock.UtcNow
            }, cancellationToken);
            return false;
        }
        catch (Exception exception)
        {
            await FailWorkflowAsync(workflow, exception.Message, BuildExceptionFailureDetails(exception), cancellationToken);
            await store.AddLogAsync(new WorkflowLog
            {
                WorkflowId = workflow.Id,
                Level = "Error",
                Message = exception.ToString(),
                CreatedAt = clock.UtcNow
            }, cancellationToken);
            return true;
        }

        return false;
    }

    private async Task<bool> StartPlanningIfReadyAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var issue = await workItems.GetIssueAsync(workflow.IssueUrl, cancellationToken);
        if (!issue.HasLabel(WorkItemWorkflowLabels.ReadyToPlan))
        {
            return false;
        }

        return await RunPlanningAsync(workflow, issue, cancellationToken);
    }

    private async Task<bool> RunPlanningAsync(
        Workflow workflow,
        WorkItem? workItem,
        CancellationToken cancellationToken,
        bool forceRefresh = false,
        IReadOnlyList<WorkItemComment>? feedbackComments = null)
    {
        var existing = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.Plan, cancellationToken);
        if (existing?.Status == TaskRunStatus.Succeeded && !forceRefresh)
        {
            workflow.PlanArtifact = existing.Output;
            var existingResult = new AgentRunResult(true, existing.ExternalId ?? $"plan-{workflow.Id:N}", existing.Output ?? string.Empty, null);
            await workItems.UpsertIssueCommentAsync(
                workflow.IssueUrl,
                PullRequestCommentMarkers.Plan(workflow.Id),
                PullRequestCommentMarkers.BuildPlanBody(workflow, existingResult),
                cancellationToken);
            await TransitionWorkflowAsync(workflow, WorkflowStatus.Implementing, WorkflowStep.Implement, "Existing planning result reused.", cancellationToken);
            return true;
        }

        if (existing?.Status == TaskRunStatus.Running)
        {
            var runningPlanIsRevision = IsPlanRevision(workflow.PlanArtifact);
            var result = await TryGetRunningAgentResultAsync(existing, cancellationToken);
            if (result is null)
            {
                return false;
            }

            await CompleteTaskRunAsync(workflow, existing, result, cancellationToken);
            await AddAgentOutputLogAsync(workflow.Id, existing, result, cancellationToken);

            if (!result.Succeeded)
            {
                await FailWorkflowAsync(workflow, result.FailureReason ?? "Planning agent failed.", BuildFailureDetails(existing, result), cancellationToken);
                return true;
            }

            await CompleteSuccessfulPlanningAsync(workflow, result, runningPlanIsRevision, cancellationToken);
            return true;
        }

        await TransitionWorkflowAsync(workflow, WorkflowStatus.Planning, WorkflowStep.Plan, "Planning started.", cancellationToken);

        var issue = workItem ?? await workItems.GetIssueAsync(workflow.IssueUrl, cancellationToken);
        var run = existing ?? new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.Plan };
        var previousOutput = forceRefresh ? run.Output : null;
        var shouldPersistPreviousPlan = string.IsNullOrWhiteSpace(workflow.PlanArtifact)
            && !string.IsNullOrWhiteSpace(previousOutput);
        if (!string.IsNullOrWhiteSpace(previousOutput))
        {
            workflow.PlanArtifact = previousOutput;
        }

        if (shouldPersistPreviousPlan)
        {
            await store.UpdateWorkflowAsync(workflow, cancellationToken);
        }

        var isRevision = IsPlanRevision(workflow.PlanArtifact);
        var prompt = await promptRenderer.RenderAsync(TaskRunKind.Plan, workflow, issue, cancellationToken);
        var branch = workflow.BaseBranch;

        run.FailureReason = null;
        await StartTaskRunAsync(workflow, run, cancellationToken);
        await TryReactToIssueAsync(workflow, run.Id, WorkflowReactionContent.PlanningStarted, cancellationToken);
        foreach (var comment in feedbackComments ?? [])
        {
            await TryReactToIssueCommentAsync(workflow, run, comment, cancellationToken);
        }

        var start = await agentRunner.StartAsync(new AgentTask(workflow.Id, TaskRunKind.Plan, prompt, workflow.RepositoryUrl, branch, workflow.Model), cancellationToken);
        await AssignExternalJobAsync(workflow, run, start.ExternalId, cancellationToken);

        if (start.CompletedResult is null)
        {
            return true;
        }

        await CompleteTaskRunAsync(workflow, run, start.CompletedResult, cancellationToken);
        await AddAgentOutputLogAsync(workflow.Id, run, start.CompletedResult, cancellationToken);

        if (!start.CompletedResult.Succeeded)
        {
            await FailWorkflowAsync(workflow, start.CompletedResult.FailureReason ?? "Planning agent failed.", BuildFailureDetails(run, start.CompletedResult), cancellationToken);
            return true;
        }

        await CompleteSuccessfulPlanningAsync(workflow, start.CompletedResult, isRevision, cancellationToken);
        return true;
    }
    private async Task<bool> RunImplementationIfReadyAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var issue = await workItems.GetIssueAsync(workflow.IssueUrl, cancellationToken);
        var feedbackComments = await GetNewIssueFeedbackForPlanAsync(workflow, issue, cancellationToken);
        if (feedbackComments.Count > 0)
        {
            return await RunPlanningAsync(workflow, issue, cancellationToken, forceRefresh: true, feedbackComments: feedbackComments);
        }

        if (!issue.HasLabel(WorkItemWorkflowLabels.ReadyToImplement))
        {
            return false;
        }

        return await RunImplementationAsync(workflow, cancellationToken);
    }
    private async Task<IReadOnlyList<WorkItemComment>> GetNewIssueFeedbackForPlanAsync(Workflow workflow, WorkItem issue, CancellationToken cancellationToken)
    {
        var planRun = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.Plan, cancellationToken);
        if (planRun?.Status == TaskRunStatus.Running)
        {
            await TransitionWorkflowAsync(workflow, WorkflowStatus.Planning, WorkflowStep.Plan, "Planning resumed for new issue feedback.", cancellationToken);
            return [];
        }

        if (planRun?.Status != TaskRunStatus.Succeeded)
        {
            return [];
        }

        return issue.UserComments
            .Where(comment => comment.UpdatedAt > planRun.UpdatedAt)
            .OrderBy(comment => comment.UpdatedAt)
            .ThenBy(comment => comment.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task CompleteSuccessfulPlanningAsync(Workflow workflow, AgentRunResult result, bool isRevision, CancellationToken cancellationToken)
    {
        workflow.PlanArtifact = result.Output;
        await workItems.UpsertIssueCommentAsync(
            workflow.IssueUrl,
            PullRequestCommentMarkers.Plan(workflow.Id),
            PullRequestCommentMarkers.BuildPlanBody(workflow, result),
            cancellationToken);
        if (isRevision)
        {
            await workItems.AddIssueCommentAsync(
                workflow.IssueUrl,
                PullRequestCommentMarkers.BuildPlanRevisionSummaryBody(workflow, result),
                cancellationToken);
        }

        await TransitionWorkflowAsync(workflow, WorkflowStatus.Implementing, WorkflowStep.Implement, isRevision ? "Planning revision completed." : "Planning completed.", cancellationToken);
    }

    private static bool IsPlanRevision(string? previousOutput)
        => !string.IsNullOrWhiteSpace(previousOutput);
    private async Task<bool> RunImplementationAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var existing = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.Implement, cancellationToken);
        if (existing?.Status == TaskRunStatus.Succeeded)
        {
            await TransitionWorkflowAsync(workflow, WorkflowStatus.CreatingPullRequest, WorkflowStep.CreatePullRequest, "Existing implementation result reused.", cancellationToken);
            return true;
        }

        if (existing?.Status == TaskRunStatus.Running)
        {
            var result = await TryGetRunningAgentResultAsync(existing, cancellationToken);
            if (result is null)
            {
                return false;
            }

            await CompleteTaskRunAsync(workflow, existing, result, cancellationToken);
            await AddAgentOutputLogAsync(workflow.Id, existing, result, cancellationToken);

            if (!result.Succeeded)
            {
                await FailWorkflowAsync(workflow, result.FailureReason ?? "Implementation agent failed.", BuildFailureDetails(existing, result), cancellationToken);
                return true;
            }

            await TransitionWorkflowAsync(workflow, WorkflowStatus.CreatingPullRequest, WorkflowStep.CreatePullRequest, "Implementation completed.", cancellationToken);
            return true;
        }

        await TryReactToIssueAsync(workflow, null, WorkflowReactionContent.ImplementationStarted, cancellationToken);
        if (workflow.BranchName is null)
        {
            workflow.BranchName = await sourceControl.CreateBranchAsync(
                new CreateBranchRequest(
                    workflow.RepositoryUrl,
                    workflow.BaseBranch,
                    $"formicae/{workflow.Id:N}",
                    workflow.IssueUrl),
                cancellationToken);
        }

        await store.UpdateWorkflowAsync(workflow, cancellationToken);
        var prompt = await promptRenderer.RenderAsync(TaskRunKind.Implement, workflow, null, cancellationToken);

        var run = existing ?? new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.Implement };
        await StartTaskRunAsync(workflow, run, cancellationToken);

        var start = await agentRunner.StartAsync(new AgentTask(workflow.Id, TaskRunKind.Implement, prompt, workflow.RepositoryUrl, workflow.BranchName, workflow.Model), cancellationToken);
        await AssignExternalJobAsync(workflow, run, start.ExternalId, cancellationToken);

        if (start.CompletedResult is null)
        {
            return true;
        }

        await CompleteTaskRunAsync(workflow, run, start.CompletedResult, cancellationToken);
        await AddAgentOutputLogAsync(workflow.Id, run, start.CompletedResult, cancellationToken);

        if (!start.CompletedResult.Succeeded)
        {
            await FailWorkflowAsync(workflow, start.CompletedResult.FailureReason ?? "Implementation agent failed.", BuildFailureDetails(run, start.CompletedResult), cancellationToken);
            return true;
        }

        await TransitionWorkflowAsync(workflow, WorkflowStatus.CreatingPullRequest, WorkflowStep.CreatePullRequest, "Implementation completed.", cancellationToken);
        return true;
    }
    private async Task<bool> CreatePullRequestAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var existing = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.CreatePullRequest, cancellationToken);
        if (existing?.Status == TaskRunStatus.Succeeded && workflow.PullRequestUrl is not null)
        {
            await TransitionWorkflowAsync(workflow, WorkflowStatus.Reviewing, WorkflowStep.AddressComments, "Existing pull request reused.", cancellationToken);
            return true;
        }

        var run = existing ?? new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.CreatePullRequest };
        await StartTaskRunAsync(workflow, run, cancellationToken);

        var taskRuns = await store.ListTaskRunsAsync(workflow.Id, cancellationToken);
        var pullRequest = await sourceControl.CreatePullRequestAsync(workflow, taskRuns, cancellationToken);
        await CompleteTaskRunAsync(workflow, run, pullRequest.Url, true, null, cancellationToken);

        workflow.PullRequestUrl = pullRequest.Url;
        await AddEventAsync(workflow.Id, run.Id, WorkflowEventTypes.PullRequestCreated, "Information", "Pull request created.", new { pullRequest.Url }, cancellationToken);
        await TransitionWorkflowAsync(workflow, WorkflowStatus.Reviewing, WorkflowStep.AddressComments, "Pull request created.", cancellationToken);
        return true;
    }

    private async Task<bool> AddressPullRequestCommentsAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var existing = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.AddressComments, cancellationToken);
        if (existing?.Status == TaskRunStatus.Running)
        {
            var result = await TryGetRunningAgentResultAsync(existing, cancellationToken);
            if (result is null)
            {
                return false;
            }

            await CompleteTaskRunAsync(workflow, existing, result, cancellationToken);
            await AddAgentOutputLogAsync(workflow.Id, existing, result, cancellationToken);

            if (!result.Succeeded)
            {
                await FailWorkflowAsync(workflow, result.FailureReason ?? "Pull request comment agent failed.", BuildFailureDetails(existing, result), cancellationToken);
                return true;
            }

            var runningResponseBody = PullRequestCommentMarkers.BuildAddressCommentsBody(workflow, result);
            await sourceControl.UpsertPullRequestCommentAsync(workflow, runningResponseBody, cancellationToken);

            await TransitionWorkflowAsync(workflow, WorkflowStatus.Completed, WorkflowStep.Done, "Workflow completed after pull request comments were addressed.", cancellationToken);
            return true;
        }

        var previousAddressedAt = existing?.Status == TaskRunStatus.Succeeded ? existing.UpdatedAt : (DateTimeOffset?)null;
        var pullRequestStatus = await sourceControl.GetPullRequestStatusAsync(workflow, cancellationToken);
        if (pullRequestStatus.IsMerged)
        {
            await TransitionWorkflowAsync(workflow, WorkflowStatus.Completed, WorkflowStep.Done, "Workflow completed because the pull request was merged.", cancellationToken);
            return true;
        }

        if (!pullRequestStatus.IsOpen)
        {
            await TransitionWorkflowAsync(workflow, WorkflowStatus.Canceled, WorkflowStep.Done, "Workflow canceled because the pull request was closed without merging.", cancellationToken);
            return true;
        }

        var comments = await sourceControl.ListPullRequestCommentsAsync(workflow, cancellationToken);
        if (comments.Count == 0)
        {
            return false;
        }

        var commentsToAddress = previousAddressedAt is null
            ? comments
            : comments.Where(comment => comment.UpdatedAt > previousAddressedAt.Value).ToArray();
        if (commentsToAddress.Count == 0)
        {
            await TransitionWorkflowAsync(workflow, WorkflowStatus.Completed, WorkflowStep.Done, "Workflow completed with no new pull request comments.", cancellationToken);
            return true;
        }

        var prompt = await promptRenderer.RenderAsync(TaskRunKind.AddressComments, workflow, null, commentsToAddress, cancellationToken);
        var contextFiles = new[]
        {
            new AgentTaskContextFile("pull-request-conversation.md", FormatPullRequestConversation(workflow, comments))
        };
        var branch = workflow.BranchName ?? throw new InvalidOperationException("Workflow branch is required before addressing pull request comments.");
        var run = existing ?? new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.AddressComments };
        await StartTaskRunAsync(workflow, run, cancellationToken);
        foreach (var comment in commentsToAddress)
        {
            await TryReactToPullRequestCommentAsync(workflow, run, comment, cancellationToken);
        }

        var start = await agentRunner.StartAsync(new AgentTask(workflow.Id, TaskRunKind.AddressComments, prompt, workflow.RepositoryUrl, branch, workflow.Model, contextFiles), cancellationToken);
        await AssignExternalJobAsync(workflow, run, start.ExternalId, cancellationToken);

        if (start.CompletedResult is null)
        {
            return true;
        }

        await CompleteTaskRunAsync(workflow, run, start.CompletedResult, cancellationToken);
        await AddAgentOutputLogAsync(workflow.Id, run, start.CompletedResult, cancellationToken);

        if (!start.CompletedResult.Succeeded)
        {
            await FailWorkflowAsync(workflow, start.CompletedResult.FailureReason ?? "Pull request comment agent failed.", BuildFailureDetails(run, start.CompletedResult), cancellationToken);
            return true;
        }

        var completedResponseBody = PullRequestCommentMarkers.BuildAddressCommentsBody(workflow, start.CompletedResult);
        await sourceControl.UpsertPullRequestCommentAsync(workflow, completedResponseBody, cancellationToken);

        await TransitionWorkflowAsync(workflow, WorkflowStatus.Completed, WorkflowStep.Done, "Workflow completed after pull request comments were addressed.", cancellationToken);
        return true;
    }
    private async Task TryReactToIssueAsync(Workflow workflow, Guid? taskRunId, string reaction, CancellationToken cancellationToken)
    {
        try
        {
            await workItems.ReactToIssueAsync(workflow.IssueUrl, reaction, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AddReactionWarningLogAsync(workflow.Id, taskRunId, exception, cancellationToken);
        }
    }

    private async Task TryReactToIssueCommentAsync(Workflow workflow, TaskRun run, WorkItemComment comment, CancellationToken cancellationToken)
    {
        try
        {
            await workItems.ReactToIssueCommentAsync(workflow.IssueUrl, comment, WorkflowReactionContent.FeedbackStarted, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AddReactionWarningLogAsync(workflow.Id, run.Id, exception, cancellationToken);
        }
    }

    private async Task TryReactToPullRequestCommentAsync(Workflow workflow, TaskRun run, PullRequestComment comment, CancellationToken cancellationToken)
    {
        try
        {
            await sourceControl.ReactToPullRequestCommentAsync(workflow, comment, WorkflowReactionContent.PullRequestCommentStarted, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AddReactionWarningLogAsync(workflow.Id, run.Id, exception, cancellationToken);
        }
    }

    private Task AddReactionWarningLogAsync(Guid workflowId, Guid? taskRunId, Exception exception, CancellationToken cancellationToken)
        => store.AddLogAsync(new WorkflowLog
        {
            WorkflowId = workflowId,
            TaskRunId = taskRunId,
            Level = "Warning",
            Message = $"GitHub reaction feedback could not be added: {exception.Message}",
            CreatedAt = clock.UtcNow
        }, cancellationToken);

    private async Task<AgentRunResult?> TryGetRunningAgentResultAsync(TaskRun run, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(run.ExternalId))
        {
            return null;
        }

        return await agentRunner.TryGetResultAsync(run.ExternalId, cancellationToken);
    }
    private async Task TransitionWorkflowAsync(
        Workflow workflow,
        WorkflowStatus status,
        WorkflowStep step,
        string message,
        CancellationToken cancellationToken,
        object? details = null)
    {
        var previousStatus = workflow.Status;
        var previousStep = workflow.CurrentStep;
        workflow.Status = status;
        workflow.CurrentStep = step;
        workflow.UpdatedAt = clock.UtcNow;
        await store.UpdateWorkflowAsync(workflow, cancellationToken);

        var type = status switch
        {
            WorkflowStatus.Completed => WorkflowEventTypes.WorkflowCompleted,
            WorkflowStatus.Failed => WorkflowEventTypes.WorkflowFailed,
            _ => WorkflowEventTypes.WorkflowTransitioned
        };
        var transitionDetails = new
        {
            fromStatus = previousStatus.ToString(),
            toStatus = status.ToString(),
            fromStep = previousStep.ToString(),
            toStep = step.ToString()
        };
        await AddEventAsync(workflow.Id, null, type, status == WorkflowStatus.Failed ? "Error" : "Information", message, details ?? transitionDetails, cancellationToken);
    }

    private async Task StartTaskRunAsync(Workflow workflow, TaskRun run, CancellationToken cancellationToken)
    {
        await EnsureWorkflowDefinitionAllowsTaskAsync(workflow, run.Kind, cancellationToken);

        var wasRunning = run.Status == TaskRunStatus.Running;
        run.Status = TaskRunStatus.Running;
        run.StartedAt ??= clock.UtcNow;
        run.CompletedAt = null;
        run.FailureReason = null;
        run.UpdatedAt = clock.UtcNow;
        await store.UpsertTaskRunAsync(run, cancellationToken);

        if (!wasRunning)
        {
            await AddEventAsync(workflow.Id, run.Id, WorkflowEventTypes.TaskStarted, "Information", $"{run.Kind} task started.", new
            {
                taskKind = run.Kind.ToString()
            }, cancellationToken);
        }
    }

    private async Task AssignExternalJobAsync(Workflow workflow, TaskRun run, string externalId, CancellationToken cancellationToken)
    {
        run.ExternalId = externalId;
        run.UpdatedAt = clock.UtcNow;
        await store.UpsertTaskRunAsync(run, cancellationToken);
        await AddEventAsync(workflow.Id, run.Id, WorkflowEventTypes.ExternalJobAssigned, "Information", $"{run.Kind} external job assigned.", new
        {
            taskKind = run.Kind.ToString(),
            externalId
        }, cancellationToken);
    }

    private Task CompleteTaskRunAsync(Workflow workflow, TaskRun run, AgentRunResult result, CancellationToken cancellationToken)
        => CompleteTaskRunAsync(workflow, run, result.Output, result.Succeeded, result.FailureReason, cancellationToken, result.ExternalId);

    private async Task CompleteTaskRunAsync(
        Workflow workflow,
        TaskRun run,
        string output,
        bool succeeded,
        string? failureReason,
        CancellationToken cancellationToken,
        string? externalId = null)
    {
        run.Status = succeeded ? TaskRunStatus.Succeeded : TaskRunStatus.Failed;
        run.ExternalId = externalId ?? run.ExternalId;
        run.Output = output;
        run.FailureReason = failureReason;
        run.StartedAt ??= clock.UtcNow;
        run.CompletedAt = clock.UtcNow;
        run.UpdatedAt = run.CompletedAt.Value;
        await store.UpsertTaskRunAsync(run, cancellationToken);

        await AddEventAsync(
            workflow.Id,
            run.Id,
            succeeded ? WorkflowEventTypes.TaskSucceeded : WorkflowEventTypes.TaskFailed,
            succeeded ? "Information" : "Error",
            succeeded ? $"{run.Kind} task succeeded." : $"{run.Kind} task failed.",
            succeeded ? new { taskKind = run.Kind.ToString(), run.ExternalId } : BuildFailureDetails(run, new AgentRunResult(false, run.ExternalId ?? string.Empty, output, failureReason)),
            cancellationToken);
    }

    private async Task FailWorkflowAsync(Workflow workflow, string reason, object? details, CancellationToken cancellationToken)
    {
        workflow.FailureReason = reason;
        await TransitionWorkflowAsync(workflow, WorkflowStatus.Failed, workflow.CurrentStep, reason, cancellationToken, details);
    }

    private static object BuildFailureDetails(TaskRun run, AgentRunResult result)
        => new
        {
            taskKind = run.Kind.ToString(),
            externalId = result.ExternalId,
            failureReason = result.FailureReason,
            outputExcerpt = Excerpt(result.Output)
        };

    private static object BuildExceptionFailureDetails(Exception exception)
        => new
        {
            exceptionType = exception.GetType().FullName,
            exception.Message,
            stackTrace = exception.ToString()
        };

    private static string Excerpt(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        const int maxLength = 4000;
        const int headLength = 1200;
        const string separator = "\n... output truncated; showing beginning and end ...\n";
        var trimmed = output.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        var tailLength = maxLength - headLength - separator.Length;
        return string.Concat(trimmed.AsSpan(0, headLength), separator, trimmed.AsSpan(trimmed.Length - tailLength));
    }

    private Task AddEventAsync(
        Guid workflowId,
        Guid? taskRunId,
        string type,
        string level,
        string message,
        object? details,
        CancellationToken cancellationToken)
        => store.AddEventAsync(new WorkflowEvent
        {
            WorkflowId = workflowId,
            TaskRunId = taskRunId,
            Type = type,
            Level = level,
            Message = message,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details),
            CreatedAt = clock.UtcNow
        }, cancellationToken);

    private static string FormatPullRequestConversation(Workflow workflow, IReadOnlyList<PullRequestComment> comments)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Pull Request Conversation");
        builder.AppendLine();
        builder.AppendLine($"Pull request: {workflow.PullRequestUrl}");
        builder.AppendLine();

        foreach (var comment in comments.OrderBy(comment => comment.UpdatedAt))
        {
            builder.AppendLine($"## {comment.Kind} by {comment.Author} at {comment.UpdatedAt:O}");
            builder.AppendLine();
            builder.AppendLine($"URL: {comment.Url}");
            builder.AppendLine();
            builder.AppendLine(comment.Body);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private Task AddAgentOutputLogAsync(Guid workflowId, TaskRun run, AgentRunResult result, CancellationToken cancellationToken)
        => store.AddLogAsync(new WorkflowLog
        {
            WorkflowId = workflowId,
            TaskRunId = run.Id,
            Level = result.Succeeded ? "Information" : "Error",
            Message = result.Output
        }, cancellationToken);

    private async Task EnsureWorkflowDefinitionAllowsTaskAsync(
        Workflow workflow,
        TaskRunKind kind,
        CancellationToken cancellationToken)
    {
        var version = workflow.WorkflowDefinitionVersionId.HasValue
            ? await store.GetWorkflowDefinitionVersionAsync(workflow.WorkflowDefinitionVersionId.Value, cancellationToken)
            : null;
        if (version is null && workflow.WorkflowDefinitionVersionId.HasValue)
        {
            throw new InvalidOperationException($"Workflow definition version '{workflow.WorkflowDefinitionVersionId}' was not found.");
        }

        if (version is null)
        {
            version = await store.GetDefaultEnabledWorkflowDefinitionVersionAsync(cancellationToken);
            if (version is null)
            {
                var (definition, defaultVersion) = DefaultWorkflowDefinitions.CreateMvp(clock.UtcNow);
                await store.EnsureDefaultWorkflowDefinitionAsync(definition, defaultVersion, cancellationToken);
                version = defaultVersion;
            }

            workflow.WorkflowDefinitionId = version.WorkflowDefinitionId;
            workflow.WorkflowDefinitionVersionId = version.Id;
            workflow.DslSchemaVersion = version.DslSchemaVersion;
            await store.UpdateWorkflowAsync(workflow, cancellationToken);
        }

        if (!version.IsEnabled)
        {
            throw new InvalidOperationException($"Workflow definition version '{version.Id}' is disabled.");
        }

        var document = WorkflowDefinitionJson.Deserialize(version.DefinitionJson);
        var validation = new WorkflowDefinitionValidator().Validate(document);
        if (!validation.IsValid || document is null)
        {
            throw new InvalidOperationException("Workflow definition version is invalid.");
        }

        var expectedUses = WorkflowDefinitionValidator.UsesFor(kind);
        var step = document.Steps.FirstOrDefault(step => string.Equals(step.Uses, expectedUses, StringComparison.Ordinal));
        if (step is null)
        {
            throw new InvalidOperationException($"Workflow definition version '{version.Id}' does not include required built-in task '{expectedUses}'.");
        }

        var expectedNext = ExpectedNextKind(kind);
        if (expectedNext is null)
        {
            return;
        }

        var nextStep = string.IsNullOrWhiteSpace(step.NextStepId)
            ? null
            : document.Steps.FirstOrDefault(candidate => string.Equals(candidate.Id, step.NextStepId, StringComparison.Ordinal));
        var expectedNextUses = WorkflowDefinitionValidator.UsesFor(expectedNext.Value);
        if (nextStep is null || !string.Equals(nextStep.Uses, expectedNextUses, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Workflow definition version '{version.Id}' does not include required transition from '{expectedUses}' to '{expectedNextUses}'.");
        }
    }

    private static TaskRunKind? ExpectedNextKind(TaskRunKind kind)
        => kind switch
        {
            TaskRunKind.Plan => TaskRunKind.Implement,
            TaskRunKind.Implement => TaskRunKind.CreatePullRequest,
            TaskRunKind.CreatePullRequest => TaskRunKind.AddressComments,
            TaskRunKind.AddressComments => null,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported task run kind.")
        };
}
