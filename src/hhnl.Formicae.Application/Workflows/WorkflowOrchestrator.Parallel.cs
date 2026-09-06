namespace hhnl.Formicae.Application.Workflows;

public sealed partial class WorkflowOrchestrator
{
    private async Task<bool> AdvanceParallelAsync(Workflow workflow, WorkflowDefinitionDocument document,
        WorkflowDefinitionStep group, CancellationToken cancellationToken)
    {
        var activation = await store.GetParallelExecutionAsync(workflow.Id, group.Id, cancellationToken);
        if (activation is null)
        {
            var issue = await workItems.GetIssueAsync(workflow.IssueUrl, cancellationToken);
            if (workflow.Status == WorkflowStatus.Queued && !issue.HasLabel(WorkItemWorkflowLabels.ReadyToPlan)) return false;
            activation = await store.UpsertParallelExecutionAsync(new WorkflowParallelExecution
            {
                WorkflowId = workflow.Id, NodeId = group.Id, EntryPlanArtifact = workflow.PlanArtifact, StartedAt = clock.UtcNow
            }, cancellationToken);
        }
        workflow.CurrentDefinitionStepId = group.Id;
        if (workflow.Status != WorkflowStatus.Planning)
            await TransitionWorkflowAsync(workflow, WorkflowStatus.Planning, WorkflowStep.Plan, $"Parallel group '{group.Id}' is running.", cancellationToken);

        if (activation.Outcome == WorkflowParallelExecutionOutcome.Succeeded)
        {
            await AdvanceDefinitionCursorAsync(workflow, $"Parallel group '{group.Id}' completed.", cancellationToken);
            return true;
        }

        var branches = WorkflowParallelDefinitions.Branches(document, group);
        var changed = false;
        foreach (var branch in branches)
        {
            string? input = activation.EntryPlanArtifact;
            foreach (var step in branch)
            {
                var run = await store.GetTaskRunExecutionAsync(workflow.Id, step.Id, null, cancellationToken);
                if (run?.Status == TaskRunStatus.Succeeded) { input = run.Output; continue; }
                if (run?.Status == TaskRunStatus.Failed) break;
                changed |= await AdvanceParallelTaskAsync(workflow, step, run, input, cancellationToken);
                // A branch starts at most one new task per tick. All other branch heads are visited before any waiting.
                break;
            }
        }

        var runs = (await store.ListTaskRunsAsync(workflow.Id, cancellationToken)).Where(run => run.LoopIteration is null)
            .ToDictionary(run => run.DefinitionStepId, StringComparer.Ordinal);
        var failures = branches.Select((branch, index) => new
        {
            Branch = index + 1,
            Failed = branch.Select(step => runs.GetValueOrDefault(step.Id)).FirstOrDefault(run => run?.Status == TaskRunStatus.Failed),
            Complete = branch.All(step => runs.GetValueOrDefault(step.Id)?.Status == TaskRunStatus.Succeeded)
        }).ToArray();
        if (failures.Any(branch => !branch.Complete && branch.Failed is null)) return changed;
        if (failures.Any(branch => branch.Failed is not null))
        {
            activation.Outcome = WorkflowParallelExecutionOutcome.Failed;
            activation.CompletedAt = clock.UtcNow;
            await store.UpsertParallelExecutionAsync(activation, cancellationToken);
            var reason = string.Join("; ", failures.Where(branch => branch.Failed is not null)
                .Select(branch => $"Parallel '{group.Id}' branch {branch.Branch}, task '{branch.Failed!.DefinitionStepId}': {branch.Failed.FailureReason}"));
            await FailWorkflowAsync(workflow, reason, new { nodeId = group.Id, failedTaskIds = failures.Where(branch => branch.Failed is not null).Select(branch => branch.Failed!.Id).ToArray() }, cancellationToken);
            return true;
        }

        var output = string.Join("\n\n", branches.Select((branch, index) => $"## Branch {index + 1}: {branch[0].DisplayName ?? branch[0].Id}\n\n{runs[branch[^1].Id].Output}"));
        var marker = $"<!-- formicae:parallel:{workflow.Id:N}:{activation.Id:N} -->";
        // Upsert before recording completion: recovery may repeat publication, but uses the same marker.
        await workItems.UpsertIssueCommentAsync(workflow.IssueUrl, marker, $"{marker}\n# Parallel planning results\n\n{output}", cancellationToken);
        workflow.PlanArtifact = output;
        await store.UpdateWorkflowAsync(workflow, cancellationToken);
        if (activation.Outcome != WorkflowParallelExecutionOutcome.Succeeded)
        {
            activation.Outcome = WorkflowParallelExecutionOutcome.Succeeded;
            activation.CompletedAt = clock.UtcNow;
            await store.UpsertParallelExecutionAsync(activation, cancellationToken);
        }
        await AdvanceDefinitionCursorAsync(workflow, $"Parallel group '{group.Id}' completed.", cancellationToken);
        return true;
    }

    private async Task<bool> AdvanceParallelTaskAsync(Workflow workflow, WorkflowDefinitionStep step, TaskRun? run,
        string? input, CancellationToken cancellationToken)
    {
        run ??= new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.Plan, DefinitionStepId = step.Id,
            CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        if (run.Status == TaskRunStatus.Running && run.ExternalId is not null)
        {
            AgentRunResult? result;
            try { result = await agentRunner.TryGetResultAsync(run.ExternalId, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // A failed status read says nothing about the external job's completion.
                await AddParallelWarningAsync(workflow, run, exception, cancellationToken);
                return false;
            }
            if (result is null) return false;
            result = ValidatePlanningResult(result);
            await CompleteTaskRunAsync(workflow, run, result, cancellationToken);
            await AddAgentOutputLogAsync(workflow.Id, run, result, cancellationToken);
            return true;
        }

        var launchAccepted = false;
        try
        {
            var issue = await workItems.GetIssueAsync(workflow.IssueUrl, cancellationToken);
            // Never mutate the tracked workflow while rendering branch-local context.
            var branchWorkflow = new Workflow
            {
                Id = workflow.Id, IssueUrl = workflow.IssueUrl, RepositoryUrl = workflow.RepositoryUrl,
                BaseBranch = workflow.BaseBranch, BranchName = workflow.BranchName, Model = workflow.Model,
                PlanArtifact = input, PullRequestUrl = workflow.PullRequestUrl,
                WorkflowDefinitionId = workflow.WorkflowDefinitionId, WorkflowDefinitionVersionId = workflow.WorkflowDefinitionVersionId,
                CurrentDefinitionStepId = step.Id, CurrentStep = WorkflowStep.Plan, Status = WorkflowStatus.Planning
            };
            var prompt = await promptRenderer.RenderAsync(TaskRunKind.Plan, branchWorkflow, issue, cancellationToken);
            run.ExecutionAttemptId ??= Guid.NewGuid();
            await StartTaskRunAsync(branchWorkflow, run, cancellationToken);
            var task = new AgentTask(workflow.Id, TaskRunKind.Plan, prompt, workflow.RepositoryUrl, workflow.BaseBranch,
                string.IsNullOrWhiteSpace(step.Model) ? workflow.Model : step.Model.Trim(),
                AiSettingsId: string.IsNullOrWhiteSpace(step.AiSettingsId) ? null : step.AiSettingsId.Trim(), ExecutionAttemptId: run.ExecutionAttemptId);
            var prepared = await PrepareAgentTaskAsync(workflow, run, task, cancellationToken);
            task = prepared.Task;
            var started = await agentRunner.StartAsync(task, cancellationToken);
            launchAccepted = true;
            await AssignExternalJobAsync(workflow, run, started.ExternalId, cancellationToken);
            await AddEventAsync(workflow.Id, run.Id, "AgentSettingsResolved", "Information",
                $"Parallel task '{step.Id}': model passed to CLI: {started.Model ?? task.Model ?? "CLI default"}.",
                new { nodeId = step.Id, aiSettingsId = started.AiSettingsId ?? task.AiSettingsId ?? AiSettings.DefaultId, model = started.Model ?? task.Model,
                    personaId = prepared.Persona?.Id ?? "default", personaRevision = prepared.Persona?.Revision ?? 1,
                    personaName = prepared.Persona?.Name ?? "Default behavior", started.ExternalId }, cancellationToken);
            if (started.CompletedResult is not null)
            {
                var result = ValidatePlanningResult(started.CompletedResult);
                await CompleteTaskRunAsync(workflow, run, result, cancellationToken);
                await AddAgentOutputLogAsync(workflow.Id, run, result, cancellationToken);
            }
            return true;
        }
        catch (Exception exception) when (launchAccepted && (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            await AddParallelWarningAsync(workflow, run, exception, cancellationToken);
            return false;
        }
        catch (Exception exception) when (IsUncertainParallelTransport(exception, cancellationToken))
        {
            // Persisted attempt identity reattaches to an existing job if its launch response was lost.
            await AddParallelWarningAsync(workflow, run, exception, cancellationToken);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await CompleteTaskRunAsync(workflow, run, "", false, exception.Message, cancellationToken);
            return true;
        }
    }

    private static bool IsUncertainParallelTransport(Exception exception, CancellationToken token)
        => !token.IsCancellationRequested && (exception is AgentLaunchUncertainException or TimeoutException or OperationCanceledException
            || exception is HttpRequestException http && (http.StatusCode is null || (int)http.StatusCode is 408 or 429 or >= 500));

    private async Task AddParallelWarningAsync(Workflow workflow, TaskRun run, Exception exception, CancellationToken token)
    {
        try
        {
            await store.AddLogAsync(new WorkflowLog { WorkflowId = workflow.Id, TaskRunId = run.Id, Level = "Warning",
                Message = $"Parallel task '{run.DefinitionStepId}' will resume its existing attempt after an orchestration error: {exception.Message}", CreatedAt = clock.UtcNow }, token);
        }
        catch (Exception) when (!token.IsCancellationRequested) { }
    }
}
