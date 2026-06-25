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
            await AdvanceAsync(workflow, cancellationToken);
            advanced++;
        }

        return advanced;
    }

    public async Task AdvanceAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        if (workflow.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Canceled)
        {
            return;
        }

        try
        {
            switch (workflow.Status)
            {
                case WorkflowStatus.Queued:
                case WorkflowStatus.Planning:
                    await RunPlanningAsync(workflow, cancellationToken);
                    break;
                case WorkflowStatus.Implementing:
                    await RunImplementationAsync(workflow, cancellationToken);
                    break;
                case WorkflowStatus.CreatingPullRequest:
                    await CreatePullRequestAsync(workflow, cancellationToken);
                    break;
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
        }
    }

    private async Task RunPlanningAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var existing = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.Plan, cancellationToken);
        if (existing?.Status == TaskRunStatus.Succeeded)
        {
            workflow.PlanArtifact = existing.Output;
            workflow.Status = WorkflowStatus.Implementing;
            workflow.CurrentStep = WorkflowStep.Implement;
            workflow.UpdatedAt = DateTimeOffset.UtcNow;
            await store.UpdateWorkflowAsync(workflow, cancellationToken);
            return;
        }

        workflow.Status = WorkflowStatus.Planning;
        workflow.CurrentStep = WorkflowStep.Plan;
        workflow.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpdateWorkflowAsync(workflow, cancellationToken);

        var issue = await workItems.GetIssueAsync(workflow.IssueUrl, cancellationToken);
        var prompt = await promptRenderer.RenderAsync(TaskRunKind.Plan, workflow, issue, cancellationToken);
        var branch = workflow.BranchName ?? $"formicae/{workflow.Id:N}";

        var run = existing ?? new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.Plan };
        run.Status = TaskRunStatus.Running;
        await store.UpsertTaskRunAsync(run, cancellationToken);

        var result = await agentRunner.RunAsync(new AgentTask(workflow.Id, TaskRunKind.Plan, prompt, workflow.RepositoryUrl, branch, workflow.Model), cancellationToken);
        CompleteTaskRun(run, result);
        await store.UpsertTaskRunAsync(run, cancellationToken);
        await AddAgentOutputLogAsync(workflow.Id, run, result, cancellationToken);

        if (!result.Succeeded)
        {
            FailWorkflow(workflow, result.FailureReason ?? "Planning agent failed.");
            await store.UpdateWorkflowAsync(workflow, cancellationToken);
            return;
        }

        workflow.PlanArtifact = result.Output;
        workflow.Status = WorkflowStatus.Implementing;
        workflow.CurrentStep = WorkflowStep.Implement;
        workflow.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpdateWorkflowAsync(workflow, cancellationToken);
    }

    private async Task RunImplementationAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var existing = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.Implement, cancellationToken);
        if (existing?.Status == TaskRunStatus.Succeeded)
        {
            workflow.Status = WorkflowStatus.CreatingPullRequest;
            workflow.CurrentStep = WorkflowStep.CreatePullRequest;
            workflow.UpdatedAt = DateTimeOffset.UtcNow;
            await store.UpdateWorkflowAsync(workflow, cancellationToken);
            return;
        }

        workflow.BranchName ??= await sourceControl.CreateBranchAsync(workflow.RepositoryUrl, workflow.BaseBranch, workflow.Id, cancellationToken);
        await store.UpdateWorkflowAsync(workflow, cancellationToken);
        var prompt = await promptRenderer.RenderAsync(TaskRunKind.Implement, workflow, null, cancellationToken);

        var run = existing ?? new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.Implement };
        run.Status = TaskRunStatus.Running;
        await store.UpsertTaskRunAsync(run, cancellationToken);

        var result = await agentRunner.RunAsync(new AgentTask(workflow.Id, TaskRunKind.Implement, prompt, workflow.RepositoryUrl, workflow.BranchName, workflow.Model), cancellationToken);
        CompleteTaskRun(run, result);
        await store.UpsertTaskRunAsync(run, cancellationToken);
        await AddAgentOutputLogAsync(workflow.Id, run, result, cancellationToken);

        if (!result.Succeeded)
        {
            FailWorkflow(workflow, result.FailureReason ?? "Implementation agent failed.");
            await store.UpdateWorkflowAsync(workflow, cancellationToken);
            return;
        }

        workflow.Status = WorkflowStatus.CreatingPullRequest;
        workflow.CurrentStep = WorkflowStep.CreatePullRequest;
        workflow.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpdateWorkflowAsync(workflow, cancellationToken);
    }

    private async Task CreatePullRequestAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var existing = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.CreatePullRequest, cancellationToken);
        if (existing?.Status == TaskRunStatus.Succeeded && workflow.PullRequestUrl is not null)
        {
            workflow.Status = WorkflowStatus.Completed;
            workflow.CurrentStep = WorkflowStep.Done;
            workflow.UpdatedAt = DateTimeOffset.UtcNow;
            await store.UpdateWorkflowAsync(workflow, cancellationToken);
            return;
        }

        var run = existing ?? new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.CreatePullRequest };
        run.Status = TaskRunStatus.Running;
        await store.UpsertTaskRunAsync(run, cancellationToken);

        var taskRuns = await store.ListTaskRunsAsync(workflow.Id, cancellationToken);
        var pullRequest = await sourceControl.CreateDraftPullRequestAsync(workflow, taskRuns, cancellationToken);
        run.Status = TaskRunStatus.Succeeded;
        run.Output = pullRequest.Url;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpsertTaskRunAsync(run, cancellationToken);

        workflow.PullRequestUrl = pullRequest.Url;
        workflow.Status = WorkflowStatus.Completed;
        workflow.CurrentStep = WorkflowStep.Done;
        workflow.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpdateWorkflowAsync(workflow, cancellationToken);
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

    private Task AddAgentOutputLogAsync(Guid workflowId, TaskRun run, AgentRunResult result, CancellationToken cancellationToken)
        => store.AddLogAsync(new WorkflowLog
        {
            WorkflowId = workflowId,
            TaskRunId = run.Id,
            Level = result.Succeeded ? "Information" : "Error",
            Message = result.Output
        }, cancellationToken);
}
