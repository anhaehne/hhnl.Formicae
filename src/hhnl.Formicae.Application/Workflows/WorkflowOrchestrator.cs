using System.Text;
using System.Text.Json;

namespace hhnl.Formicae.Application.Workflows;

public sealed partial class WorkflowOrchestrator(
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
            var definition = await ResolveDefinitionAsync(workflow, cancellationToken);
            var current = definition.Steps.SingleOrDefault(step => step.Id == (workflow.CurrentDefinitionStepId ?? definition.StartStepId));
            if (current?.Uses == WorkflowDecisionDefinitions.Uses)
                return await AdvanceDecisionAsync(workflow, definition, current, cancellationToken);
            if (current?.Uses == WorkflowParallelDefinitions.Uses)
            {
                try { return await AdvanceParallelAsync(workflow, definition, current, cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // Store/provider failures cannot abandon siblings whose jobs may still be running.
                    try { await store.AddLogAsync(new WorkflowLog { WorkflowId = workflow.Id, Level = "Warning",
                        Message = $"Parallel group '{current.Id}' will resume after an orchestration error: {exception.Message}", CreatedAt = clock.UtcNow }, cancellationToken); }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
                    return false;
                }
            }
            var context = await ResolveExecutionContextAsync(workflow, cancellationToken);
            if (context is null)
            {
                return true;
            }

            switch (context.Kind)
            {
                case TaskRunKind.Plan:
                    return workflow.Status == WorkflowStatus.Queued
                        ? await StartPlanningIfReadyAsync(workflow, cancellationToken)
                        : await RunPlanningAsync(workflow, null, cancellationToken);
                case TaskRunKind.Implement:
                    return await RunImplementationIfReadyAsync(workflow, cancellationToken);
                case TaskRunKind.CreatePullRequest:
                    return await CreatePullRequestAsync(workflow, cancellationToken);
                case TaskRunKind.AddressComments:
                    return await AddressPullRequestCommentsAsync(workflow, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        var existing = await GetCurrentTaskRunAsync(workflow, cancellationToken);
        if (existing?.Status == TaskRunStatus.Succeeded && !forceRefresh)
        {
            var existingResult = ValidatePlanningResult(new AgentRunResult(true, existing.ExternalId ?? $"plan-{workflow.Id:N}", existing.Output ?? string.Empty, null));
            if (!existingResult.Succeeded)
            {
                if (string.Equals(workflow.PlanArtifact?.Trim(), existingResult.Output.Trim(), StringComparison.Ordinal))
                {
                    workflow.PlanArtifact = null;
                }

                await CompleteTaskRunAsync(workflow, existing, existingResult, cancellationToken);
                await AddAgentOutputLogAsync(workflow.Id, existing, existingResult, cancellationToken);
                await FailWorkflowAsync(workflow, existingResult.FailureReason!, BuildFailureDetails(existing, existingResult), cancellationToken);
                return true;
            }

            workflow.PlanArtifact = existingResult.Output;
            await workItems.UpsertIssueCommentAsync(
                workflow.IssueUrl,
                PullRequestCommentMarkers.Plan(workflow.Id),
                PullRequestCommentMarkers.BuildPlanBody(workflow, existingResult),
                cancellationToken);
            await AdvanceDefinitionCursorAsync(workflow, "Existing planning result reused.", cancellationToken);
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

            result = ValidatePlanningResult(result);
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
        var run = existing ?? await CreateCurrentTaskRunAsync(workflow, TaskRunKind.Plan, cancellationToken);
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

        var start = await StartAgentTaskAsync(workflow, run, new AgentTask(workflow.Id, TaskRunKind.Plan, prompt, workflow.RepositoryUrl, branch, workflow.Model), cancellationToken);
        await AssignExternalJobAsync(workflow, run, start.ExternalId, cancellationToken);

        if (start.CompletedResult is null)
        {
            return true;
        }

        var completedResult = ValidatePlanningResult(start.CompletedResult);
        await CompleteTaskRunAsync(workflow, run, completedResult, cancellationToken);
        await AddAgentOutputLogAsync(workflow.Id, run, completedResult, cancellationToken);

        if (!completedResult.Succeeded)
        {
            await FailWorkflowAsync(workflow, completedResult.FailureReason ?? "Planning agent failed.", BuildFailureDetails(run, completedResult), cancellationToken);
            return true;
        }

        await CompleteSuccessfulPlanningAsync(workflow, completedResult, isRevision, cancellationToken);
        return true;
    }

    private static AgentRunResult ValidatePlanningResult(AgentRunResult result)
    {
        if (!result.Succeeded)
        {
            return result;
        }

        var output = result.Output.Trim();
        if (output.Length == 0)
        {
            return result with
            {
                Succeeded = false,
                FailureReason = "Planning agent completed without returning an implementation plan."
            };
        }

        var describesModeBlocker = output.Contains("plan mode", StringComparison.OrdinalIgnoreCase)
            && (output.Contains("must", StringComparison.OrdinalIgnoreCase)
                || output.Contains("require", StringComparison.OrdinalIgnoreCase)
                || output.Contains("only", StringComparison.OrdinalIgnoreCase)
                || output.Contains("cannot", StringComparison.OrdinalIgnoreCase));
        var retryMarkers = new[] { "resend", "run", "use", "enter", "retry", "again" };
        var requestsInteractiveRetry = output.Contains("/plan", StringComparison.OrdinalIgnoreCase)
            && retryMarkers.Any(marker => output.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (output.Length <= 2000 && describesModeBlocker && requestsInteractiveRetry)
        {
            return result with
            {
                Succeeded = false,
                FailureReason = "Planning agent requested an interactive Plan-mode retry instead of returning an implementation plan."
            };
        }

        return result;
    }
    private async Task<bool> RunImplementationIfReadyAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var issue = await workItems.GetIssueAsync(workflow.IssueUrl, cancellationToken);
        var feedbackComments = await GetNewIssueFeedbackForPlanAsync(workflow, issue, cancellationToken);
        if (feedbackComments.Count > 0)
        {
            var document = await ResolveDefinitionAsync(workflow, cancellationToken);
            var latestPlan = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.Plan, cancellationToken);
            workflow.CurrentDefinitionStepId = document.Steps.First(step => step.Uses == WorkflowDefinitionValidator.UsesFor(TaskRunKind.Plan)
                && (string.IsNullOrEmpty(latestPlan?.DefinitionStepId) || step.Id == latestPlan.DefinitionStepId)).Id;
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
        if (planRun is not null)
        {
            var document = await ResolveDefinitionAsync(workflow, cancellationToken);
            if (document.Steps.Any(step => step.Uses == WorkflowDecisionDefinitions.Uses))
                return []; // Committed decisions must not be replayed by legacy feedback rewinds.
            if (document.Steps.Where(step => step.Parallel is not null)
                .SelectMany(group => WorkflowParallelDefinitions.Branches(document, group)).SelectMany(branch => branch)
                .Any(step => step.Id == planRun.DefinitionStepId))
                return []; // Parallel plans are immutable group results; never rewind into a branch through the legacy cursor.
        }
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

        await AdvanceDefinitionCursorAsync(workflow, isRevision ? "Planning revision completed." : "Planning completed.", cancellationToken);
    }

    private static bool IsPlanRevision(string? previousOutput)
        => !string.IsNullOrWhiteSpace(previousOutput);
    private async Task<bool> RunImplementationAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var existing = await GetCurrentTaskRunAsync(workflow, cancellationToken);
        if (existing?.Status == TaskRunStatus.Succeeded)
        {
            await AdvanceDefinitionCursorAsync(workflow, "Existing implementation result reused.", cancellationToken);
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

            await AdvanceDefinitionCursorAsync(workflow, "Implementation completed.", cancellationToken);
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

        var run = existing ?? await CreateCurrentTaskRunAsync(workflow, TaskRunKind.Implement, cancellationToken);
        await StartTaskRunAsync(workflow, run, cancellationToken);

        var start = await StartAgentTaskAsync(workflow, run, new AgentTask(workflow.Id, TaskRunKind.Implement, prompt, workflow.RepositoryUrl, workflow.BranchName, workflow.Model), cancellationToken);
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

        await AdvanceDefinitionCursorAsync(workflow, "Implementation completed.", cancellationToken);
        return true;
    }
    private async Task<bool> CreatePullRequestAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var existing = await GetCurrentTaskRunAsync(workflow, cancellationToken);
        if (existing?.Status == TaskRunStatus.Succeeded && workflow.PullRequestUrl is not null)
        {
            await AdvanceDefinitionCursorAsync(workflow, "Existing pull request reused.", cancellationToken);
            return true;
        }

        var run = existing ?? await CreateCurrentTaskRunAsync(workflow, TaskRunKind.CreatePullRequest, cancellationToken);
        await StartTaskRunAsync(workflow, run, cancellationToken);

        var taskRuns = await store.ListTaskRunsAsync(workflow.Id, cancellationToken);
        var pullRequest = await sourceControl.CreatePullRequestAsync(workflow, taskRuns, cancellationToken);
        await CompleteTaskRunAsync(workflow, run, pullRequest.Url, true, null, cancellationToken);

        workflow.PullRequestUrl = pullRequest.Url;
        await AddEventAsync(workflow.Id, run.Id, WorkflowEventTypes.PullRequestCreated, "Information", "Pull request created.", new { pullRequest.Url }, cancellationToken);
        await AdvanceDefinitionCursorAsync(workflow, "Pull request created.", cancellationToken);
        return true;
    }

    private async Task<bool> AddressPullRequestCommentsAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var existing = await GetCurrentTaskRunAsync(workflow, cancellationToken);
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

            await AdvanceDefinitionCursorAsync(workflow, "Workflow completed after pull request comments were addressed.", cancellationToken);
            return true;
        }

        var previousAddressedAt = existing?.Status == TaskRunStatus.Succeeded ? existing.UpdatedAt : (DateTimeOffset?)null;
        var pullRequestStatus = await sourceControl.GetPullRequestStatusAsync(workflow, cancellationToken);
        if (pullRequestStatus.IsMerged)
        {
            await AdvanceDefinitionCursorAsync(workflow, "Workflow completed because the pull request was merged.", cancellationToken);
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
            await AdvanceDefinitionCursorAsync(workflow, "Workflow completed with no new pull request comments.", cancellationToken);
            return true;
        }

        var prompt = await promptRenderer.RenderAsync(TaskRunKind.AddressComments, workflow, null, commentsToAddress, cancellationToken);
        var contextFiles = new[]
        {
            new AgentTaskContextFile("pull-request-conversation.md", FormatPullRequestConversation(workflow, comments))
        };
        var branch = workflow.BranchName ?? throw new InvalidOperationException("Workflow branch is required before addressing pull request comments.");
        var run = existing ?? await CreateCurrentTaskRunAsync(workflow, TaskRunKind.AddressComments, cancellationToken);
        await StartTaskRunAsync(workflow, run, cancellationToken);
        foreach (var comment in commentsToAddress)
        {
            await TryReactToPullRequestCommentAsync(workflow, run, comment, cancellationToken);
        }

        var start = await StartAgentTaskAsync(workflow, run, new AgentTask(workflow.Id, TaskRunKind.AddressComments, prompt, workflow.RepositoryUrl, branch, workflow.Model, contextFiles), cancellationToken);
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

        await AdvanceDefinitionCursorAsync(workflow, "Workflow completed after pull request comments were addressed.", cancellationToken);
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

    private async Task<AgentRunStartResult> StartAgentTaskAsync(Workflow workflow, TaskRun run, AgentTask task, CancellationToken cancellationToken)
    {
        try
        {
            var prepared = await PrepareAgentTaskAsync(workflow, run, task, cancellationToken);
            task = prepared.Task;
            var started = await agentRunner.StartAsync(task, cancellationToken);
            await AddEventAsync(workflow.Id, run.Id, "AgentSettingsResolved", "Information",
                $"AI configuration: {started.AiSettingsId ?? task.AiSettingsId ?? AiSettings.DefaultId}; model passed to CLI: {started.Model ?? task.Model ?? "CLI default"}.",
                new { aiSettingsId = started.AiSettingsId ?? task.AiSettingsId ?? AiSettings.DefaultId, model = started.Model ?? task.Model,
                    personaId = prepared.Persona?.Id ?? "default", personaRevision = prepared.Persona?.Revision ?? 1,
                    personaName = prepared.Persona?.Name ?? "Default behavior", started.ExternalId }, cancellationToken);
            return started;
        }
        catch (Exception exception)
        {
            await CompleteTaskRunAsync(workflow, run, string.Empty, false, exception.Message, cancellationToken);
            throw;
        }
    }

    private sealed record PreparedAgentTask(AgentTask Task, PersonaSnapshot? Persona);

    private async Task<PreparedAgentTask> PrepareAgentTaskAsync(Workflow workflow, TaskRun run, AgentTask task, CancellationToken cancellationToken)
    {
        var document = await ResolveDefinitionAsync(workflow, cancellationToken);
        var step = document.Steps.SingleOrDefault(step => step.Id == run.DefinitionStepId);
        var persona = step?.PersonaSnapshot;
        return new(task with
        {
            AiSettingsId = string.IsNullOrWhiteSpace(step?.AiSettingsId) ? null : step.AiSettingsId.Trim(),
            Model = string.IsNullOrWhiteSpace(step?.Model) ? task.Model : step.Model.Trim(),
            Prompt = PersonaPromptComposer.Compose(task.Prompt, persona)
        }, persona);
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

    private sealed record ExecutionContext(WorkflowDefinitionDocument Document, WorkflowDefinitionStep Step, TaskRunKind Kind, WorkflowDefinitionLoop? Loop, int? Iteration);

    private async Task<ExecutionContext?> ResolveExecutionContextAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var document = await ResolveDefinitionAsync(workflow, cancellationToken);
        if (workflow.CurrentDefinitionStepId is null)
        {
            var legacyKind = workflow.CurrentStep switch
            {
                WorkflowStep.Plan => TaskRunKind.Plan,
                WorkflowStep.Implement => TaskRunKind.Implement,
                WorkflowStep.CreatePullRequest => TaskRunKind.CreatePullRequest,
                WorkflowStep.AddressComments => TaskRunKind.AddressComments,
                _ => (TaskRunKind?)null
            };
            workflow.CurrentDefinitionStepId = legacyKind.HasValue
                ? document.Steps.FirstOrDefault(item => item.Uses == WorkflowDefinitionValidator.UsesFor(legacyKind.Value))?.Id
                : document.StartStepId;
        }
        var step = document.Steps.SingleOrDefault(item => item.Id == workflow.CurrentDefinitionStepId)
            ?? throw new InvalidOperationException($"Workflow step '{workflow.CurrentDefinitionStepId}' was not found in its immutable definition.");
        if (!WorkflowDefinitionValidator.TryMapUsesToTaskKind(step.Uses, out var kind))
            throw new InvalidOperationException($"Workflow step '{step.Id}' uses unsupported task '{step.Uses}'.");

        var loop = document.Loops?.SingleOrDefault(item => item.BodyStepIds.Contains(step.Id, StringComparer.Ordinal));
        int? iterationNumber = null;
        if (loop is not null)
        {
            var history = (await store.ListLoopIterationsAsync(workflow.Id, cancellationToken))
                .Where(item => item.LoopId == loop.Id).OrderBy(item => item.IterationNumber).ToArray();
            var active = history.LastOrDefault(item => item.Outcome == WorkflowLoopIterationOutcome.Running);
            if (active is null)
            {
                var nextIteration = history.Length == 0 ? 1 : history.Max(item => item.IterationNumber) + 1;
                var loopStartedAt = history.FirstOrDefault()?.StartedAt;
                string? failureCode = null;
                if (nextIteration > loop.MaxIterations) failureCode = "LOOP_MAX_ITERATIONS_EXCEEDED";
                else if (loop.TimeoutSeconds.HasValue && loopStartedAt.HasValue
                    && clock.UtcNow - loopStartedAt.Value >= TimeSpan.FromSeconds(loop.TimeoutSeconds.Value))
                    failureCode = "LOOP_TIMEOUT_EXCEEDED";
                if (failureCode is not null)
                {
                    var reason = $"{failureCode}: Loop '{loop.Id}' cannot start iteration {nextIteration}.";
                    await AddEventAsync(workflow.Id, null, WorkflowEventTypes.LoopGuardrailFailed, "Error", reason,
                        new { code = failureCode, loopId = loop.Id, iteration = nextIteration, loop.MaxIterations, loop.TimeoutSeconds, loopStartedAt }, cancellationToken);
                    await FailWorkflowAsync(workflow, reason, new { code = failureCode, loopId = loop.Id, iteration = nextIteration }, cancellationToken);
                    return null;
                }
                active = new WorkflowLoopIteration
                {
                    WorkflowId = workflow.Id,
                    LoopId = loop.Id,
                    IterationNumber = nextIteration,
                    StartedAt = clock.UtcNow
                };
                await store.UpsertLoopIterationAsync(active, cancellationToken);
            }
            iterationNumber = active.IterationNumber;
        }

        return new ExecutionContext(document, step, kind, loop, iterationNumber);
    }

    private async Task<WorkflowDefinitionDocument> ResolveDefinitionAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var version = workflow.WorkflowDefinitionVersionId.HasValue
            ? await store.GetWorkflowDefinitionVersionAsync(workflow.WorkflowDefinitionVersionId.Value, cancellationToken)
            : null;
        if (version is null && workflow.WorkflowDefinitionVersionId.HasValue)
            throw new InvalidOperationException($"Workflow definition version '{workflow.WorkflowDefinitionVersionId}' was not found.");
        if (version is null)
        {
            version = await store.GetDefaultEnabledWorkflowDefinitionVersionAsync(cancellationToken);
            if (version is null)
            {
                var defaults = DefaultWorkflowDefinitions.CreateMvp(clock.UtcNow);
                await store.EnsureDefaultWorkflowDefinitionAsync(defaults.Definition, defaults.Version, cancellationToken);
                version = defaults.Version;
            }
            workflow.WorkflowDefinitionId = version.WorkflowDefinitionId;
            workflow.WorkflowDefinitionVersionId = version.Id;
            workflow.DslSchemaVersion = version.DslSchemaVersion;
        }
        if (!version.IsEnabled) throw new InvalidOperationException($"Workflow definition version '{version.Id}' is disabled.");
        var document = WorkflowDefinitionJson.Deserialize(version.DefinitionJson);
        var validation = new WorkflowDefinitionValidator().Validate(document);
        if (document is null || !validation.IsValid) throw new InvalidOperationException("Workflow definition version is invalid.");
        var personaValidation = PersonaDefinitions.ValidateRuntime(document);
        if (!personaValidation.IsValid) throw new InvalidOperationException(string.Join(" ", personaValidation.Errors.Select(error => error.Message)));
        return WorkflowNodeDefinitions.Normalize(document);
    }

    private async Task<TaskRun?> GetCurrentTaskRunAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var context = await ResolveExecutionContextAsync(workflow, cancellationToken);
        if (context is null) return null;
        var execution = await store.GetTaskRunExecutionAsync(workflow.Id, context.Step.Id, context.Iteration, cancellationToken);
        if (execution is not null) return execution;
        var legacy = await store.GetTaskRunAsync(workflow.Id, context.Kind, cancellationToken);
        return legacy is { DefinitionStepId.Length: 0 } ? legacy : null;
    }

    private async Task<TaskRun> CreateCurrentTaskRunAsync(Workflow workflow, TaskRunKind kind, CancellationToken cancellationToken)
    {
        var context = await ResolveExecutionContextAsync(workflow, cancellationToken)
            ?? throw new InvalidOperationException("The workflow cannot create a task run after a loop guardrail failure.");
        return new TaskRun { WorkflowId = workflow.Id, Kind = kind, DefinitionStepId = context.Step.Id, LoopIteration = context.Iteration, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
    }

    private async Task AdvanceDefinitionCursorAsync(Workflow workflow, string message, CancellationToken cancellationToken)
    {
        var document = await ResolveDefinitionAsync(workflow, cancellationToken);
        var step = document.Steps.Single(item => item.Id == workflow.CurrentDefinitionStepId);
        var loop = document.Loops?.SingleOrDefault(item => item.BodyStepIds.Contains(step.Id, StringComparer.Ordinal));
        string? nextStepId;
        if (loop is not null && string.Equals(loop.BodyStepIds[^1], step.Id, StringComparison.Ordinal))
        {
            var active = (await store.ListLoopIterationsAsync(workflow.Id, cancellationToken))
                .Last(item => item.LoopId == loop.Id && item.Outcome == WorkflowLoopIterationOutcome.Running);
            active.Outcome = WorkflowLoopIterationOutcome.Succeeded;
            active.CompletedAt = clock.UtcNow;
            await store.UpsertLoopIterationAsync(active, cancellationToken);
            nextStepId = active.IterationNumber < loop.RepeatCount ? loop.BodyStepIds[0] : loop.ExitStepId;
        }
        else
        {
            nextStepId = step.NextStepId;
        }

        if (string.IsNullOrWhiteSpace(nextStepId))
        {
            workflow.CurrentDefinitionStepId = null;
            await TransitionWorkflowAsync(workflow, WorkflowStatus.Completed, WorkflowStep.Done, message, cancellationToken);
            return;
        }
        workflow.CurrentDefinitionStepId = nextStepId;
        var next = document.Steps.Single(item => item.Id == nextStepId);
        var nextKind = TaskRunKind.Plan;
        if (next.Uses != WorkflowParallelDefinitions.Uses && next.Uses != WorkflowDecisionDefinitions.Uses)
            WorkflowDefinitionValidator.TryMapUsesToTaskKind(next.Uses, out nextKind);
        await TransitionWorkflowAsync(workflow, StatusFor(nextKind), StepFor(nextKind), message, cancellationToken);
    }

    private static WorkflowStatus StatusFor(TaskRunKind kind) => kind switch
    {
        TaskRunKind.Plan => WorkflowStatus.Planning,
        TaskRunKind.Implement => WorkflowStatus.Implementing,
        TaskRunKind.CreatePullRequest => WorkflowStatus.CreatingPullRequest,
        TaskRunKind.AddressComments => WorkflowStatus.Reviewing,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static WorkflowStep StepFor(TaskRunKind kind) => kind switch
    {
        TaskRunKind.Plan => WorkflowStep.Plan,
        TaskRunKind.Implement => WorkflowStep.Implement,
        TaskRunKind.CreatePullRequest => WorkflowStep.CreatePullRequest,
        TaskRunKind.AddressComments => WorkflowStep.AddressComments,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

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
        var step = document.Steps.FirstOrDefault(step => string.Equals(step.Id, workflow.CurrentDefinitionStepId, StringComparison.Ordinal));
        if (step is null || !string.Equals(step.Uses, expectedUses, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Workflow definition step '{workflow.CurrentDefinitionStepId}' does not use required built-in task '{expectedUses}'.");
        }
    }
}
