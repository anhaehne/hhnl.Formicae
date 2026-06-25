using System.Text;

namespace hhnl.Formicae.Application.Workflows;

public sealed class WorkflowOrchestrator(
    IWorkflowStore store,
    IWorkItemProvider workItems,
    ISourceControlProvider sourceControl,
    IAgentRunner agentRunner,
    IPromptRenderer promptRenderer)
{
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
        catch (Exception exception)
        {
            workflow.Status = WorkflowStatus.Failed;
            workflow.FailureReason = exception.Message;
            workflow.UpdatedAt = DateTimeOffset.UtcNow;
            await store.UpdateWorkflowAsync(workflow, cancellationToken);
            await store.AddLogAsync(new WorkflowLog
            {
                WorkflowId = workflow.Id,
                Level = "Error",
                Message = exception.Message
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

    private async Task<bool> RunPlanningAsync(Workflow workflow, WorkItem? workItem, CancellationToken cancellationToken)
    {
        var existing = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.Plan, cancellationToken);
        if (existing?.Status == TaskRunStatus.Succeeded)
        {
            workflow.PlanArtifact = existing.Output;
            var existingResult = new AgentRunResult(true, existing.ExternalId ?? $"plan-{workflow.Id:N}", existing.Output ?? string.Empty, null);
            await workItems.UpsertIssueCommentAsync(
                workflow.IssueUrl,
                PullRequestCommentMarkers.Plan(workflow.Id),
                PullRequestCommentMarkers.BuildPlanBody(workflow, existingResult),
                cancellationToken);
            workflow.Status = WorkflowStatus.Implementing;
            workflow.CurrentStep = WorkflowStep.Implement;
            workflow.UpdatedAt = DateTimeOffset.UtcNow;
            await store.UpdateWorkflowAsync(workflow, cancellationToken);
            return true;
        }

        if (existing?.Status == TaskRunStatus.Running)
        {
            var result = await TryGetRunningAgentResultAsync(existing, cancellationToken);
            if (result is null)
            {
                return false;
            }

            CompleteTaskRun(existing, result);
            await store.UpsertTaskRunAsync(existing, cancellationToken);
            await AddAgentOutputLogAsync(workflow.Id, existing, result, cancellationToken);

            if (!result.Succeeded)
            {
                FailWorkflow(workflow, result.FailureReason ?? "Planning agent failed.");
                await store.UpdateWorkflowAsync(workflow, cancellationToken);
                return true;
            }

            workflow.PlanArtifact = result.Output;
            await workItems.UpsertIssueCommentAsync(
                workflow.IssueUrl,
                PullRequestCommentMarkers.Plan(workflow.Id),
                PullRequestCommentMarkers.BuildPlanBody(workflow, result),
                cancellationToken);
            workflow.Status = WorkflowStatus.Implementing;
            workflow.CurrentStep = WorkflowStep.Implement;
            workflow.UpdatedAt = DateTimeOffset.UtcNow;
            await store.UpdateWorkflowAsync(workflow, cancellationToken);
            return true;
        }

        workflow.Status = WorkflowStatus.Planning;
        workflow.CurrentStep = WorkflowStep.Plan;
        workflow.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpdateWorkflowAsync(workflow, cancellationToken);

        var issue = workItem ?? await workItems.GetIssueAsync(workflow.IssueUrl, cancellationToken);
        var prompt = await promptRenderer.RenderAsync(TaskRunKind.Plan, workflow, issue, cancellationToken);
        var branch = workflow.BranchName ?? $"formicae/{workflow.Id:N}";

        var run = existing ?? new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.Plan };
        run.Status = TaskRunStatus.Running;
        await store.UpsertTaskRunAsync(run, cancellationToken);
        await workItems.ReactToIssueAsync(workflow.IssueUrl, WorkflowReactionContent.Started, cancellationToken);

        var start = await agentRunner.StartAsync(new AgentTask(workflow.Id, TaskRunKind.Plan, prompt, workflow.RepositoryUrl, branch, workflow.Model), cancellationToken);
        run.ExternalId = start.ExternalId;
        run.UpdatedAt = DateTimeOffset.UtcNow;

        if (start.CompletedResult is null)
        {
            await store.UpsertTaskRunAsync(run, cancellationToken);
            return true;
        }

        CompleteTaskRun(run, start.CompletedResult);
        await store.UpsertTaskRunAsync(run, cancellationToken);
        await AddAgentOutputLogAsync(workflow.Id, run, start.CompletedResult, cancellationToken);

        if (!start.CompletedResult.Succeeded)
        {
            FailWorkflow(workflow, start.CompletedResult.FailureReason ?? "Planning agent failed.");
            await store.UpdateWorkflowAsync(workflow, cancellationToken);
            return true;
        }

        workflow.PlanArtifact = start.CompletedResult.Output;
        await workItems.UpsertIssueCommentAsync(
            workflow.IssueUrl,
            PullRequestCommentMarkers.Plan(workflow.Id),
            PullRequestCommentMarkers.BuildPlanBody(workflow, start.CompletedResult),
            cancellationToken);
        workflow.Status = WorkflowStatus.Implementing;
        workflow.CurrentStep = WorkflowStep.Implement;
        workflow.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpdateWorkflowAsync(workflow, cancellationToken);
        return true;
    }
    private async Task<bool> RunImplementationIfReadyAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var issue = await workItems.GetIssueAsync(workflow.IssueUrl, cancellationToken);
        if (!issue.HasLabel(WorkItemWorkflowLabels.ReadyToImplement))
        {
            return false;
        }

        return await RunImplementationAsync(workflow, cancellationToken);
    }

    private async Task<bool> RunImplementationAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var existing = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.Implement, cancellationToken);
        if (existing?.Status == TaskRunStatus.Succeeded)
        {
            workflow.Status = WorkflowStatus.CreatingPullRequest;
            workflow.CurrentStep = WorkflowStep.CreatePullRequest;
            workflow.UpdatedAt = DateTimeOffset.UtcNow;
            await store.UpdateWorkflowAsync(workflow, cancellationToken);
            return true;
        }

        if (existing?.Status == TaskRunStatus.Running)
        {
            var result = await TryGetRunningAgentResultAsync(existing, cancellationToken);
            if (result is null)
            {
                return false;
            }

            CompleteTaskRun(existing, result);
            await store.UpsertTaskRunAsync(existing, cancellationToken);
            await AddAgentOutputLogAsync(workflow.Id, existing, result, cancellationToken);

            if (!result.Succeeded)
            {
                FailWorkflow(workflow, result.FailureReason ?? "Implementation agent failed.");
                await store.UpdateWorkflowAsync(workflow, cancellationToken);
                return true;
            }

            workflow.Status = WorkflowStatus.CreatingPullRequest;
            workflow.CurrentStep = WorkflowStep.CreatePullRequest;
            workflow.UpdatedAt = DateTimeOffset.UtcNow;
            await store.UpdateWorkflowAsync(workflow, cancellationToken);
            return true;
        }

        await workItems.ReactToIssueAsync(workflow.IssueUrl, WorkflowReactionContent.Started, cancellationToken);
        workflow.BranchName ??= await sourceControl.CreateBranchAsync(workflow.RepositoryUrl, workflow.BaseBranch, workflow.Id, cancellationToken);
        await store.UpdateWorkflowAsync(workflow, cancellationToken);
        var prompt = await promptRenderer.RenderAsync(TaskRunKind.Implement, workflow, null, cancellationToken);

        var run = existing ?? new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.Implement };
        run.Status = TaskRunStatus.Running;
        await store.UpsertTaskRunAsync(run, cancellationToken);

        var start = await agentRunner.StartAsync(new AgentTask(workflow.Id, TaskRunKind.Implement, prompt, workflow.RepositoryUrl, workflow.BranchName, workflow.Model), cancellationToken);
        run.ExternalId = start.ExternalId;
        run.UpdatedAt = DateTimeOffset.UtcNow;

        if (start.CompletedResult is null)
        {
            await store.UpsertTaskRunAsync(run, cancellationToken);
            return true;
        }

        CompleteTaskRun(run, start.CompletedResult);
        await store.UpsertTaskRunAsync(run, cancellationToken);
        await AddAgentOutputLogAsync(workflow.Id, run, start.CompletedResult, cancellationToken);

        if (!start.CompletedResult.Succeeded)
        {
            FailWorkflow(workflow, start.CompletedResult.FailureReason ?? "Implementation agent failed.");
            await store.UpdateWorkflowAsync(workflow, cancellationToken);
            return true;
        }

        workflow.Status = WorkflowStatus.CreatingPullRequest;
        workflow.CurrentStep = WorkflowStep.CreatePullRequest;
        workflow.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpdateWorkflowAsync(workflow, cancellationToken);
        return true;
    }
    private async Task<bool> CreatePullRequestAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var existing = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.CreatePullRequest, cancellationToken);
        if (existing?.Status == TaskRunStatus.Succeeded && workflow.PullRequestUrl is not null)
        {
            workflow.Status = WorkflowStatus.Reviewing;
            workflow.CurrentStep = WorkflowStep.AddressComments;
            workflow.UpdatedAt = DateTimeOffset.UtcNow;
            await store.UpdateWorkflowAsync(workflow, cancellationToken);
            return true;
        }

        var run = existing ?? new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.CreatePullRequest };
        run.Status = TaskRunStatus.Running;
        await store.UpsertTaskRunAsync(run, cancellationToken);

        var taskRuns = await store.ListTaskRunsAsync(workflow.Id, cancellationToken);
        var pullRequest = await sourceControl.CreatePullRequestAsync(workflow, taskRuns, cancellationToken);
        run.Status = TaskRunStatus.Succeeded;
        run.Output = pullRequest.Url;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpsertTaskRunAsync(run, cancellationToken);

        workflow.PullRequestUrl = pullRequest.Url;
        workflow.Status = WorkflowStatus.Reviewing;
        workflow.CurrentStep = WorkflowStep.AddressComments;
        workflow.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpdateWorkflowAsync(workflow, cancellationToken);
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

            CompleteTaskRun(existing, result);
            await store.UpsertTaskRunAsync(existing, cancellationToken);
            await AddAgentOutputLogAsync(workflow.Id, existing, result, cancellationToken);

            if (!result.Succeeded)
            {
                FailWorkflow(workflow, result.FailureReason ?? "Pull request comment agent failed.");
                await store.UpdateWorkflowAsync(workflow, cancellationToken);
                return true;
            }

            var runningResponseBody = PullRequestCommentMarkers.BuildAddressCommentsBody(workflow, result);
            await sourceControl.UpsertPullRequestCommentAsync(workflow, runningResponseBody, cancellationToken);

            workflow.Status = WorkflowStatus.Completed;
            workflow.CurrentStep = WorkflowStep.Done;
            workflow.UpdatedAt = DateTimeOffset.UtcNow;
            await store.UpdateWorkflowAsync(workflow, cancellationToken);
            return true;
        }

        var previousAddressedAt = existing?.Status == TaskRunStatus.Succeeded ? existing.UpdatedAt : (DateTimeOffset?)null;
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
            workflow.Status = WorkflowStatus.Completed;
            workflow.CurrentStep = WorkflowStep.Done;
            workflow.UpdatedAt = DateTimeOffset.UtcNow;
            await store.UpdateWorkflowAsync(workflow, cancellationToken);
            return true;
        }

        var prompt = await promptRenderer.RenderAsync(TaskRunKind.AddressComments, workflow, null, commentsToAddress, cancellationToken);
        var contextFiles = new[]
        {
            new AgentTaskContextFile("pull-request-conversation.md", FormatPullRequestConversation(workflow, comments))
        };
        var branch = workflow.BranchName ?? throw new InvalidOperationException("Workflow branch is required before addressing pull request comments.");
        var run = existing ?? new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.AddressComments };
        run.Status = TaskRunStatus.Running;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpsertTaskRunAsync(run, cancellationToken);
        foreach (var comment in commentsToAddress)
        {
            await sourceControl.ReactToPullRequestCommentAsync(workflow, comment, WorkflowReactionContent.Started, cancellationToken);
        }

        var start = await agentRunner.StartAsync(new AgentTask(workflow.Id, TaskRunKind.AddressComments, prompt, workflow.RepositoryUrl, branch, workflow.Model, contextFiles), cancellationToken);
        run.ExternalId = start.ExternalId;
        run.UpdatedAt = DateTimeOffset.UtcNow;

        if (start.CompletedResult is null)
        {
            await store.UpsertTaskRunAsync(run, cancellationToken);
            return true;
        }

        CompleteTaskRun(run, start.CompletedResult);
        await store.UpsertTaskRunAsync(run, cancellationToken);
        await AddAgentOutputLogAsync(workflow.Id, run, start.CompletedResult, cancellationToken);

        if (!start.CompletedResult.Succeeded)
        {
            FailWorkflow(workflow, start.CompletedResult.FailureReason ?? "Pull request comment agent failed.");
            await store.UpdateWorkflowAsync(workflow, cancellationToken);
            return true;
        }

        var completedResponseBody = PullRequestCommentMarkers.BuildAddressCommentsBody(workflow, start.CompletedResult);
        await sourceControl.UpsertPullRequestCommentAsync(workflow, completedResponseBody, cancellationToken);

        workflow.Status = WorkflowStatus.Completed;
        workflow.CurrentStep = WorkflowStep.Done;
        workflow.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpdateWorkflowAsync(workflow, cancellationToken);
        return true;
    }
    private async Task<AgentRunResult?> TryGetRunningAgentResultAsync(TaskRun run, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(run.ExternalId))
        {
            return new AgentRunResult(false, run.Id.ToString("N"), string.Empty, "Running task run does not have an external job id.");
        }

        return await agentRunner.TryGetResultAsync(run.ExternalId, cancellationToken);
    }
    private static void CompleteTaskRun(TaskRun run, AgentRunResult result)
    {
        run.Status = result.Succeeded ? TaskRunStatus.Succeeded : TaskRunStatus.Failed;
        run.ExternalId = result.ExternalId;
        run.Output = result.Output;
        run.FailureReason = result.FailureReason;
        run.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void FailWorkflow(Workflow workflow, string reason)
    {
        workflow.Status = WorkflowStatus.Failed;
        workflow.FailureReason = reason;
        workflow.UpdatedAt = DateTimeOffset.UtcNow;
    }

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
}
