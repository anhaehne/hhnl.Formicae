namespace hhnl.Formicae.Application.Workflows;

public sealed partial class WorkflowOrchestrator
{
    private async Task<bool> AdvanceGraphAsync(Workflow workflow, WorkflowDefinitionDocument document, CancellationToken token)
    {
        // The existing durable activation stores the entry and its frozen input across restarts and retries.
        WorkflowParallelExecution? activation = null;
        foreach (var step in document.Steps)
        {
            activation = await store.GetParallelExecutionAsync(workflow.Id, step.Id, token);
            if (activation is not null) break;
        }
        if (activation is null)
        {
            var entry = workflow.CurrentDefinitionStepId ?? document.StartStepId;
            if (document.Steps.Single(step => step.Id == entry).Uses == "builtins.plan"
                && !(await workItems.GetIssueAsync(workflow.IssueUrl, token)).HasLabel(WorkItemWorkflowLabels.ReadyToPlan)) return false;
            activation = await store.UpsertParallelExecutionAsync(new WorkflowParallelExecution
            {
                WorkflowId = workflow.Id, NodeId = entry, EntryPlanArtifact = workflow.PlanArtifact, StartedAt = clock.UtcNow
            }, token);
        }
        var active = WorkflowGraphDefinitions.Reachable(document, activation.NodeId);
        var tasks = document.Steps.Where(step => active.Contains(step.Id) && step.Trigger is null).ToArray();
        var runs = (await store.ListTaskRunsAsync(workflow.Id, token)).Where(run => run.LoopIteration is null)
            .ToDictionary(run => run.DefinitionStepId, StringComparer.Ordinal);
        var failed = runs.Values.FirstOrDefault(run => active.Contains(run.DefinitionStepId) && run.Status == TaskRunStatus.Failed);
        if (failed is not null)
        {
            await FailWorkflowAsync(workflow, failed.FailureReason ?? $"Task '{failed.DefinitionStepId}' failed.", null, token);
            return true;
        }
        var runnable = tasks.Where(step => runs.GetValueOrDefault(step.Id)?.Status != TaskRunStatus.Succeeded
            && (step.Uses == WorkflowEndDefinitions.Uses
                ? step.Id == activation.NodeId || tasks.Any(parent => WorkflowGraphDefinitions.Successors(parent).Contains(step.Id, StringComparer.Ordinal)
                    && runs.GetValueOrDefault(parent.Id)?.Status == TaskRunStatus.Succeeded)
                : tasks.Where(parent => WorkflowGraphDefinitions.Successors(parent).Contains(step.Id, StringComparer.Ordinal))
                    .All(parent => runs.GetValueOrDefault(parent.Id)?.Status == TaskRunStatus.Succeeded))).ToArray();
        var changed = false;
        foreach (var step in runnable.OrderByDescending(step => step.Uses == WorkflowEndDefinitions.Uses))
        {
            token.ThrowIfCancellationRequested();
            if (step.Uses == WorkflowEndDefinitions.Uses) return await RunEndNodeAsync(workflow, step, token);
            workflow.CurrentDefinitionStepId = step.Id;
            workflow.PlanArtifact = GraphPlanInput(document, step.Id, runs, activation.EntryPlanArtifact);
            WorkflowDefinitionValidator.TryMapUsesToTaskKind(step.Uses, out var kind);
            workflow.CurrentStep = StepFor(kind);
            switch (kind)
            {
                case TaskRunKind.Plan:
                    changed |= await AdvanceParallelTaskAsync(workflow, step, runs.GetValueOrDefault(step.Id), workflow.PlanArtifact, token);
                    break;
                case TaskRunKind.CreateBranch: changed |= await RunCreateBranchTaskAsync(workflow, step, token); break;
                case TaskRunKind.AddIssueComment: changed |= await RunIssueCommentTaskAsync(workflow, step, token); break;
                case TaskRunKind.Wait: changed |= await RunWaitNodeAsync(workflow, step, token); break;
                case TaskRunKind.Script: changed |= await RunScriptTaskAsync(workflow, step, token); break;
                case TaskRunKind.Custom: changed |= await RunCustomTaskAsync(workflow, step, token); break;
                case TaskRunKind.Implement: changed |= await RunImplementationIfReadyAsync(workflow, token); break;
                case TaskRunKind.CreatePullRequest: changed |= await CreatePullRequestAsync(workflow, token); break;
                case TaskRunKind.AddressComments: changed |= await AddressPullRequestCommentsAsync(workflow, token); break;
            }
            if (workflow.Status is WorkflowStatus.Failed or WorkflowStatus.Canceled) return true;
        }
        runs = (await store.ListTaskRunsAsync(workflow.Id, token)).Where(run => run.LoopIteration is null)
            .ToDictionary(run => run.DefinitionStepId, StringComparer.Ordinal);
        failed = runs.Values.FirstOrDefault(run => active.Contains(run.DefinitionStepId) && run.Status == TaskRunStatus.Failed);
        if (failed is not null)
        {
            await FailWorkflowAsync(workflow, failed.FailureReason ?? $"Task '{failed.DefinitionStepId}' failed.", null, token);
            return true;
        }
        if (tasks.All(step => runs.GetValueOrDefault(step.Id)?.Status == TaskRunStatus.Succeeded))
        {
            activation.Outcome = WorkflowParallelExecutionOutcome.Succeeded;
            activation.CompletedAt = clock.UtcNow;
            await store.UpsertParallelExecutionAsync(activation, token);
            workflow.PlanArtifact = GraphPlanInput(document, null, runs, activation.EntryPlanArtifact);
            workflow.CurrentDefinitionStepId = null;
            await TransitionWorkflowAsync(workflow, WorkflowStatus.Completed, WorkflowStep.Done, "All workflow tasks completed.", token);
            return true;
        }
        workflow.CurrentDefinitionStepId = activation.NodeId;
        if (workflow.Status != WorkflowStatus.Running)
            await TransitionWorkflowAsync(workflow, WorkflowStatus.Running, WorkflowStep.Custom, "Waiting for workflow dependencies.", token);
        else await store.UpdateWorkflowAsync(workflow, token);
        return changed;
    }

    private static string? GraphPlanInput(WorkflowDefinitionDocument document, string? consumer,
        IReadOnlyDictionary<string, TaskRun> runs, string? entryPlan)
    {
        var plans = document.Steps.Where(step => step.Uses == "builtins.plan"
            && runs.GetValueOrDefault(step.Id)?.Status == TaskRunStatus.Succeeded
            && step.Id != consumer && (consumer is null || WorkflowGraphDefinitions.Reachable(document, step.Id).Contains(consumer))).ToArray();
        var latest = plans.Where(plan => !plans.Any(other => other.Id != plan.Id
            && WorkflowGraphDefinitions.Reachable(document, plan.Id).Contains(other.Id))).ToArray();
        return latest.Length switch
        {
            0 => entryPlan,
            1 => runs[latest[0].Id].Output,
            _ => string.Join("\n\n", latest.Select(plan => $"## {plan.DisplayName ?? plan.Id}\n\n{runs[plan.Id].Output}"))
        };
    }
}
