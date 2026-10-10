namespace hhnl.Formicae.Application.Workflows;

public sealed partial class WorkflowOrchestrator
{
    private readonly Dictionary<string, Task<WorkItem>> schedulingIssues = [];
    private readonly Dictionary<Guid, Task<PullRequestStatus>> schedulingPullRequests = [];
    private readonly Dictionary<Guid, Task<IReadOnlyList<PullRequestComment>>> schedulingComments = [];
    private bool advancingBatch;
    private IReadOnlyList<Workflow>? batchWorkflows;
    private IReadOnlyDictionary<Guid, WorkflowDefinition>? batchDefinitions;
    private WorkflowConcurrencySettings? batchConcurrencySettings;
    private void ResetSchedulingReads()
    {
        schedulingIssues.Clear(); schedulingPullRequests.Clear(); schedulingComments.Clear();
        batchWorkflows = null; batchDefinitions = null; batchConcurrencySettings = null;
    }
    private Task<WorkItem> GetSchedulingIssueAsync(string url, CancellationToken token)
    {
        if (!schedulingIssues.TryGetValue(url, out var value)) schedulingIssues[url] = value = workItems.GetIssueAsync(url, token);
        return value;
    }
    private Task<PullRequestStatus> GetSchedulingPullRequestStatusAsync(Workflow workflow, CancellationToken token)
    {
        if (!schedulingPullRequests.TryGetValue(workflow.Id, out var value)) schedulingPullRequests[workflow.Id] = value = sourceControl.GetPullRequestStatusAsync(workflow, token);
        return value;
    }
    private Task<IReadOnlyList<PullRequestComment>> GetSchedulingPullRequestCommentsAsync(Workflow workflow, CancellationToken token)
    {
        if (!schedulingComments.TryGetValue(workflow.Id, out var value)) schedulingComments[workflow.Id] = value = sourceControl.ListPullRequestCommentsAsync(workflow, token);
        return value;
    }

    // Callers hold IWorkflowOrchestrationLock across admission and advancement.
    // Persisted slots are reservations, not expiring leases: existing workers survive restarts.
    public async Task<bool> AdvanceAsync(Workflow workflow, CancellationToken token)
    {
        if (!advancingBatch) ResetSchedulingReads();
        await ReconcileAdmissionAsync(token, refreshQueue: !advancingBatch);
        var persisted = await store.GetWorkflowAsync(workflow.Id, token);
        if (persisted is not null)
        {
            workflow.HasExecutionSlot = persisted.HasExecutionSlot;
            workflow.EnqueuedAt = persisted.EnqueuedAt;
        }
        var cleanup = workflow.CancelRequestedAt is not null || workflow.IsPaused
            || workflow.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Canceled;
        if (!workflow.HasExecutionSlot && !cleanup) return false;
        var changed = await AdvanceCoreAsync(workflow, token);
        // Decision replay may receive an old snapshot. Refresh the durable row without writing that snapshot back.
        var current = await store.GetWorkflowAsync(workflow.Id, token) ?? workflow;
        await RefreshQueueAsync(current, token, pollProviders: false);
        workflow.HasExecutionSlot = current.HasExecutionSlot;
        workflow.EnqueuedAt = current.EnqueuedAt;
        return changed;
    }

    private async Task ReconcileAdmissionAsync(CancellationToken token, bool refreshQueue = true)
    {
        var workflows = advancingBatch
            ? batchWorkflows ??= await store.ListRunnableWorkflowsAsync(token)
            : await store.ListRunnableWorkflowsAsync(token);
        if (refreshQueue)
            foreach (var workflow in workflows) await RefreshQueueAsync(workflow, token);
        // The distributed lock prevents configuration changes during this tick. Reload on every new tick.
        var settings = advancingBatch
            ? batchConcurrencySettings ??= await store.GetConcurrencySettingsAsync(token)
            : await store.GetConcurrencySettingsAsync(token);
        var definitions = advancingBatch
            ? batchDefinitions ??= (await store.ListWorkflowDefinitionsAsync(token)).ToDictionary(item => item.Id)
            : (await store.ListWorkflowDefinitionsAsync(token)).ToDictionary(item => item.Id);
        var active = workflows.Count(item => item.HasExecutionSlot);
        var counts = workflows.Where(item => item.HasExecutionSlot).GroupBy(item => item.WorkflowDefinitionId)
            .ToDictionary(group => group.Key ?? Guid.Empty, group => group.Count());
        foreach (var workflow in workflows.Where(item => !item.HasExecutionSlot && item.EnqueuedAt is not null)
            .OrderBy(item => item.EnqueuedAt).ThenBy(item => item.CreatedAt).ThenBy(item => item.Id))
        {
            if (settings.MaxActiveInstances is { } global && active >= global) break;
            var id = workflow.WorkflowDefinitionId ?? Guid.Empty;
            if (definitions.GetValueOrDefault(id)?.MaxActiveInstances is { } limit && counts.GetValueOrDefault(id) >= limit)
                continue;
            workflow.HasExecutionSlot = true;
            workflow.EnqueuedAt = null;
            workflow.UpdatedAt = clock.UtcNow;
            await store.UpdateWorkflowAsync(workflow, token);
            active++;
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }
    }

    private async Task RefreshQueueAsync(Workflow workflow, CancellationToken token, bool pollProviders = true)
    {
        bool ready;
        var runs = await store.ListTaskRunsAsync(workflow.Id, token);
        var workers = runs.Any(run => run.Status == TaskRunStatus.Running || run.RuntimeCleanupPending);
        try { ready = workers || await HasRunnableWorkAsync(workflow, runs, token, pollProviders); }
        catch (WorkItemProviderUnavailableException exception)
        {
            await store.AddLogAsync(new WorkflowLog { WorkflowId = workflow.Id, Level = "Warning",
                Message = $"Work item provider is temporarily unavailable: {exception.Message}", CreatedAt = clock.UtcNow }, token);
            ready = false;
        }
        catch (HttpRequestException) { ready = false; }
        catch (InvalidOperationException) { ready = true; } // Let normal advancement record invalid definitions.
        var slot = workflow.HasExecutionSlot;
        var queued = workflow.EnqueuedAt;
        if (workers) { workflow.HasExecutionSlot = true; workflow.EnqueuedAt = null; }
        else if (!ready) { workflow.HasExecutionSlot = false; workflow.EnqueuedAt = null; }
        else if (!slot && queued is null)
            workflow.EnqueuedAt = clock.UtcNow;
        if (slot != workflow.HasExecutionSlot || queued != workflow.EnqueuedAt)
        {
            workflow.UpdatedAt = clock.UtcNow;
            await store.UpdateWorkflowAsync(workflow, token);
        }
    }

    private async Task<bool> HasRunnableWorkAsync(Workflow workflow, IReadOnlyList<TaskRun> runs, CancellationToken token, bool pollProviders)
    {
        if (workflow.CancelRequestedAt is not null || workflow.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Canceled)
            return false;
        // Claims are durable independently of admission, including while paused or globally full.
        foreach (var wait in await store.ListWaitsAsync(workflow.Id, token))
            if (!wait.IsCanceled && runs.Any(run => run.ExecutionAttemptId == wait.ExecutionAttemptId && run.Status == TaskRunStatus.Waiting))
                await store.ClaimWaitEventAsync(wait.Id, token);
        if (workflow.IsPaused) return false;
        var document = await ResolveDefinitionAsync(workflow, token);
        IEnumerable<WorkflowDefinitionStep> candidates;
        if (WorkflowCycleDefinitions.HasCycles(document))
        {
            if (workflow.CycleExecutionJson is null) return true;
            var state = WorkflowCycleDefinitions.State(workflow);
            if (WorkflowGraphDefinitions.IsGraph(document) && PendingCycleNodes(document, state).Any()) return true;
            candidates = document.Steps.Where(step => state.Active.ContainsKey(step.Id));
        }
        else if (WorkflowGraphDefinitions.IsGraph(document))
        {
            var activation = (await store.ListParallelExecutionsAsync(workflow.Id, token)).FirstOrDefault();
            var entry = activation?.NodeId ?? workflow.CurrentDefinitionStepId ?? document.StartStepId;
            var reachable = WorkflowGraphDefinitions.Reachable(document, entry);
            var tasks = document.Steps.Where(step => reachable.Contains(step.Id) && step.Trigger is null).ToArray();
            var completed = runs.Where(run => run.LoopIteration is null && run.Status == TaskRunStatus.Succeeded)
                .Select(run => run.DefinitionStepId).ToHashSet();
            if (tasks.All(step => completed.Contains(step.Id))) return true;
            candidates = tasks.Where(step => !completed.Contains(step.Id)
                && (step.Uses == WorkflowEndDefinitions.Uses
                    ? step.Id == entry || tasks.Any(parent => WorkflowGraphDefinitions.Successors(parent).Contains(step.Id) && completed.Contains(parent.Id))
                    : tasks.Where(parent => WorkflowGraphDefinitions.Successors(parent).Contains(step.Id)).All(parent => completed.Contains(parent.Id))));
        }
        else
        {
            var id = workflow.CurrentDefinitionStepId;
            if (id is null && workflow.CurrentStep != WorkflowStep.None)
                id = document.Steps.FirstOrDefault(step => WorkflowDefinitionValidator.TryMapUsesToTaskKind(step.Uses, out var kind)
                    && StepFor(kind) == workflow.CurrentStep)?.Id;
            candidates = document.Steps.Where(step => step.Id == (id ?? document.StartStepId));
        }
        foreach (var step in candidates)
        {
            var visit = WorkflowCycleDefinitions.Visit(workflow, step.Id);
            var run = runs.Where(run => run.DefinitionStepId == step.Id && (visit is null || run.LoopIteration == visit))
                .OrderByDescending(run => run.CreatedAt).FirstOrDefault();
            if (run?.Status == TaskRunStatus.Waiting)
            {
                var wait = run.ExecutionAttemptId is { } attempt ? await store.GetWaitAsync(attempt, token) : null;
                if (wait?.MatchedEventId is not null || wait is null) return true;
                continue;
            }
            if (step.Uses == "builtins.plan" && workflow.Status == WorkflowStatus.Queued && run is null)
            {
                if ((await GetSchedulingIssueAsync(workflow.IssueUrl, token)).HasLabel(WorkItemWorkflowLabels.ReadyToPlan)) return true;
                continue;
            }
            if (step.Uses == "builtins.implement" && run?.Status != TaskRunStatus.Succeeded)
            {
                if (!pollProviders && !schedulingIssues.ContainsKey(workflow.IssueUrl)) continue;
                var issue = await GetSchedulingIssueAsync(workflow.IssueUrl, token);
                if (issue.HasLabel(WorkItemWorkflowLabels.ReadyToImplement)
                    || (await GetNewIssueFeedbackForPlanAsync(workflow, issue, token)).Count > 0) return true;
                continue;
            }
            if (step.Uses == "builtins.address-comments" && run?.Status != TaskRunStatus.Succeeded)
            {
                if (!pollProviders && !schedulingPullRequests.ContainsKey(workflow.Id)) continue;
                var status = await GetSchedulingPullRequestStatusAsync(workflow, token);
                if (!status.IsOpen || status.IsMerged || (await GetSchedulingPullRequestCommentsAsync(workflow, token)).Count > 0) return true;
                continue;
            }
            if (step.Parallel is not null && workflow.Status == WorkflowStatus.Queued)
            {
                if ((await GetSchedulingIssueAsync(workflow.IssueUrl, token)).HasLabel(WorkItemWorkflowLabels.ReadyToPlan)) return true;
                continue;
            }
            return true;
        }
        return false;
    }
}
