namespace hhnl.Formicae.Application.Workflows;

public sealed partial class WorkflowOrchestrator
{
    private static string CycleEdge(string source, string target) => System.Text.Json.JsonSerializer.Serialize(new[] { source, target });

    private async Task<bool> AdvanceCycleAsync(Workflow workflow, WorkflowDefinitionDocument document, CancellationToken token)
    {
        var state = WorkflowCycleDefinitions.State(workflow);
        if (workflow.CycleExecutionJson is null)
        {
            var entry = workflow.CurrentDefinitionStepId ?? document.StartStepId;
            state.Entry = entry;
            ActivateCycleNode(state, entry);
            WorkflowCycleDefinitions.Save(workflow, state);
            await store.UpdateWorkflowAsync(workflow, token);
        }
        if (WorkflowGraphDefinitions.IsGraph(document))
        {
            var nodes = document.Steps.ToDictionary(step => step.Id);
            var entry = document.StartStepId;
            var feedback = new HashSet<string>(); var visited = new HashSet<string>(); var visiting = new HashSet<string>();
            void Visit(string id)
            {
                if (!nodes.ContainsKey(id) || !visited.Add(id)) return;
                visiting.Add(id);
                foreach (var next in WorkflowGraphDefinitions.Successors(nodes[id]))
                    if (visiting.Contains(next)) feedback.Add(CycleEdge(id, next)); else Visit(next);
                visiting.Remove(id);
            }
            // Selected-entry traversal defines initial versus feedback dependencies.
            Visit(state.Entry ?? entry);
            foreach (var id in nodes.Keys) Visit(id);
            var components = WorkflowCycleDefinitions.Components(document);
            var reachable = WorkflowGraphDefinitions.Reachable(document, state.Entry ?? entry);
            foreach (var step in document.Steps.Where(step => step.Trigger is null && reachable.Contains(step.Id) && !state.Active.ContainsKey(step.Id)))
            {
                var incoming = document.Steps.Where(parent => parent.Trigger is null && reachable.Contains(parent.Id) && WorkflowGraphDefinitions.Successors(parent).Contains(step.Id)).ToArray();
                bool Fresh(WorkflowDefinitionStep parent) => state.Delivered.GetValueOrDefault(CycleEdge(parent.Id, step.Id))
                    > state.Consumed.GetValueOrDefault(CycleEdge(parent.Id, step.Id));
                if (!incoming.Any(Fresh)) continue;
                var repeated = state.Completed.GetValueOrDefault(step.Id) > 0;
                bool Ready(WorkflowDefinitionStep parent)
                {
                    var edge = CycleEdge(parent.Id, step.Id);
                    if (feedback.Contains(edge)) return !repeated || Fresh(parent);
                    // Inputs entering a cyclic region remain available across its repeated passes.
                    if (components[parent.Id] != components[step.Id] && (components.Count(item => item.Value == components[step.Id]) > 1 || WorkflowGraphDefinitions.Successors(step).Contains(step.Id)))
                        return state.Delivered.GetValueOrDefault(edge) > 0;
                    return Fresh(parent);
                }
                if (!incoming.All(Ready)) continue;
                foreach (var parent in incoming) state.Consumed[CycleEdge(parent.Id, step.Id)] = state.Delivered.GetValueOrDefault(CycleEdge(parent.Id, step.Id));
                ActivateCycleNode(state, step.Id);
            }
            WorkflowCycleDefinitions.Save(workflow, state);
            await store.UpdateWorkflowAsync(workflow, token);
        }
        await RecordCycleLoopsAsync(workflow, document, state, token);
        var changed = false;
        foreach (var id in state.Active.Keys.ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (workflow.IsPaused || workflow.CancelRequestedAt is not null) break;
            var step = document.Steps.Single(item => item.Id == id);
            workflow.CurrentDefinitionStepId = id;
            if (step.Decision is not null) { changed |= await AdvanceDecisionAsync(workflow, document, step, token); continue; }
            if (step.Parallel is not null) { changed |= await AdvanceParallelAsync(workflow, document, step, token); continue; }
            WorkflowDefinitionValidator.TryMapUsesToTaskKind(step.Uses, out var kind);
            workflow.CurrentStep = StepFor(kind);
            if (kind == TaskRunKind.Plan && workflow.Status == WorkflowStatus.Queued
                && !(await workItems.GetIssueAsync(workflow.IssueUrl, token)).HasLabel(WorkItemWorkflowLabels.ReadyToPlan)) continue;
            changed |= kind switch
            {
                TaskRunKind.Plan => await RunPlanningAsync(workflow, null, token),
                TaskRunKind.Implement => await RunImplementationIfReadyAsync(workflow, token),
                TaskRunKind.CreatePullRequest => await CreatePullRequestAsync(workflow, token),
                TaskRunKind.AddressComments => await AddressPullRequestCommentsAsync(workflow, token),
                TaskRunKind.Wait => await RunWaitNodeAsync(workflow, step, token),
                TaskRunKind.Script => await RunScriptTaskAsync(workflow, step, token),
                TaskRunKind.Custom => await RunCustomTaskAsync(workflow, step, token),
                TaskRunKind.AddIssueComment => await RunIssueCommentTaskAsync(workflow, step, token),
                _ => false
            };
            if (workflow.Status is WorkflowStatus.Failed or WorkflowStatus.Canceled or WorkflowStatus.Completed) return true;
            if (WorkflowCycleDefinitions.State(workflow).Active.ContainsKey(id)
                && await GetCurrentTaskRunAsync(workflow, token) is { Status: TaskRunStatus.Succeeded })
            { await AdvanceDefinitionCursorAsync(workflow, "Cycle visit completed.", token); changed = true; }
        }
        return changed;
    }

    private async Task RecordCycleLoopsAsync(Workflow workflow, WorkflowDefinitionDocument document, WorkflowCycleState state, CancellationToken token)
    {
        var history = await store.ListLoopIterationsAsync(workflow.Id, token);
        foreach (var loop in document.Loops ?? [])
        {
            foreach (var item in history.Where(item => item.LoopId == loop.Id && item.Outcome == WorkflowLoopIterationOutcome.Running
                && state.Completed.GetValueOrDefault(loop.BodyStepIds[^1]) >= item.IterationNumber))
            {
                item.Outcome = WorkflowLoopIterationOutcome.Succeeded; item.CompletedAt = clock.UtcNow;
                await store.UpsertLoopIterationAsync(item, token);
            }
            if (state.Active.TryGetValue(loop.BodyStepIds[0], out var active)
                && !history.Any(item => item.LoopId == loop.Id && item.IterationNumber == active.Visit))
                await store.UpsertLoopIterationAsync(new() { WorkflowId = workflow.Id, LoopId = loop.Id,
                    IterationNumber = active.Visit, StartedAt = active.StartedAt }, token);
        }
    }

    private void ActivateCycleNode(WorkflowCycleState state, string id)
        => state.Active[id] = new(state.Completed.GetValueOrDefault(id) + 1, new(state.Completed, StringComparer.Ordinal), clock.UtcNow);

    private void CompleteCycleNode(Workflow workflow, WorkflowDefinitionDocument document, string id, string? selectedTarget = null)
    {
        var state = WorkflowCycleDefinitions.State(workflow);
        if (!state.Active.Remove(id, out var activation)) return;
        state.Completed[id] = activation.Visit;
        var step = document.Steps.Single(item => item.Id == id);
        var targets = selectedTarget is not null ? new[] { selectedTarget } : WorkflowGraphDefinitions.Successors(step);
        if (!WorkflowGraphDefinitions.IsGraph(document))
        {
            var loop = document.Loops?.FirstOrDefault(item => item.BodyStepIds.Contains(id));
            if (loop is not null)
            {
                var pass = state.Loops.GetValueOrDefault(loop.Id) ?? new(1, activation.StartedAt);
                if (loop.TimeoutSeconds is { } timeout && clock.UtcNow - pass.StartedAt >= TimeSpan.FromSeconds(timeout))
                    throw new InvalidOperationException($"LOOP_TIMEOUT_EXCEEDED: Loop '{loop.Id}' exceeded its configured timeout.");
                if (loop.BodyStepIds[^1] == id)
                {
                    if (pass.Iteration < loop.RepeatCount)
                    { state.Loops[loop.Id] = pass with { Iteration = pass.Iteration + 1 }; targets = new[] { loop.BodyStepIds[0] }; }
                    else { state.Loops.Remove(loop.Id); targets = new[] { loop.ExitStepId }; }
                }
                else state.Loops[loop.Id] = pass;
            }
            foreach (var target in targets) ActivateCycleNode(state, target);
            workflow.CurrentDefinitionStepId = targets.FirstOrDefault();
        }
        else
            foreach (var target in targets)
            {
                var edge = CycleEdge(id, target); state.Delivered[edge] = state.Delivered.GetValueOrDefault(edge) + 1;
            }
        WorkflowCycleDefinitions.Save(workflow, state);
    }

    private async Task AdvanceCycleCursorAsync(Workflow workflow, WorkflowDefinitionDocument document, string message, CancellationToken token)
    {
        var id = workflow.CurrentDefinitionStepId!;
        CompleteCycleNode(workflow, document, id);
        if (!WorkflowGraphDefinitions.IsGraph(document) && workflow.CurrentDefinitionStepId is null)
            await TransitionWorkflowAsync(workflow, WorkflowStatus.Completed, WorkflowStep.Done, message, token);
        else
            await TransitionWorkflowAsync(workflow, WorkflowStatus.Running, WorkflowStep.Custom, message, token);
    }
}
