namespace hhnl.Formicae.Application.Workflows;

/// <summary>Validates exclusive outer DAGs, treating bounded control regions as atomic vertices.</summary>
public static class WorkflowDecisionDefinitions
{
    public const string Uses = "builtins.decision";
    private sealed record Link(string Source, string Target, string Role);

    public static WorkflowDefinitionValidationResult Validate(WorkflowDefinitionDocument document)
    {
        var errors = new List<WorkflowDefinitionValidationError>();
        void Error(string id, string message) => errors.Add(new("definition.decision.graph.invalid", message, "steps", id));
        if (document.Steps.Count == 0 || document.Steps.Any(n => string.IsNullOrWhiteSpace(n.Id))
            || document.Steps.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() != document.Steps.Count)
            return new([new("definition.decision.graph.invalid", "Node IDs must be nonempty and unique; at least one node is required.", "steps")]);
        var nodes = document.Steps.ToDictionary(n => n.Id, StringComparer.Ordinal);
        if (document.Triggers?.Count > 0 || document.Loops?.Count > 0)
            Error(document.StartStepId, "v1alpha3 stores triggers and loops on nodes.");
        bool ValidTarget(string? id) => id is not null && nodes.TryGetValue(id, out var target) && target.Uses != WorkflowNodeDefinitions.TriggerUses;
        if (!ValidTarget(document.StartStepId)) Error(document.StartStepId, "Manual start must reference an execution node.");
        var links = new List<Link>();
        foreach (var node in nodes.Values)
        {
            var isTask = !string.IsNullOrWhiteSpace(node.Uses) && WorkflowDefinitionValidator.TryMapUsesToTaskKind(node.Uses, out _);
            var isDecision = node.Uses == Uses;
            var isLoop = node.Uses == WorkflowNodeDefinitions.LoopUses;
            var isParallel = node.Uses == WorkflowParallelDefinitions.Uses;
            var isTrigger = node.Uses == WorkflowNodeDefinitions.TriggerUses;
            if (!isTask && !isDecision && !isLoop && !isParallel && !isTrigger)
                Error(node.Id, "Unsupported workflow node type.");
            if ((!isDecision && node.Decision is not null) || (!isLoop && node.Loop is not null)
                || (!isParallel && node.Parallel is not null) || (!isTrigger && node.Trigger is not null))
                Error(node.Id, "Node settings must match its type.");
            if (!isTask && (node.AiSettingsId is not null || node.Model is not null))
                Error(node.Id, "Control nodes cannot have agent settings.");
            if (!isTask && node.NextStepPort is not null)
                Error(node.Id, "Control outputs cannot connect to Return or Join inputs.");
            if (node.NextStepPort is not (null or "return" or "join")) Error(node.Id, "Unknown connection port.");
            if (node.NextStepId is { } next)
            {
                if (!ValidTarget(next)) Error(node.Id, "Next must reference an execution node.");
                else links.Add(new(node.Id, next, node.NextStepPort ?? "next"));
            }
            if (isDecision)
            {
                if (node.NextStepId is not null) Error(node.Id, "Decision nodes use True and False outputs, not Next.");
                if (node.Decision is not { } decision) { Error(node.Id, "Decision settings are required."); continue; }
                if (decision.TrueStepId == decision.FalseStepId) Error(node.Id, "True and False must reference different targets.");
                foreach (var (role, target) in new[] { ("true", decision.TrueStepId), ("false", decision.FalseStepId) })
                {
                    if (!ValidTarget(target)) Error(node.Id, $"Decision {role} must reference an execution node.");
                    else links.Add(new(node.Id, target, role));
                }
                foreach (var error in WorkflowDecisionEvaluator.Validate(decision.Condition).Errors)
                    errors.Add(error with { NodeId = node.Id });
            }
            if (isLoop)
            {
                if (node.Loop is not { } loop) { Error(node.Id, "Loop settings are required."); continue; }
                if (!ValidTarget(node.NextStepId)) Error(node.Id, "Loop Exit is required.");
                if (loop.RepeatCount <= 0 || loop.MaxIterations <= 0 || loop.RepeatCount > loop.MaxIterations || loop.TimeoutSeconds is <= 0)
                    Error(node.Id, "Loop bounds and timeout must be positive; repeat count cannot exceed maximum iterations.");
                if (!ValidTarget(loop.BodyStepId)) Error(node.Id, "Loop Body is required.");
                else links.Add(new(node.Id, loop.BodyStepId, "body"));
            }
            if (isParallel)
            {
                if (node.Parallel?.BranchStepIds is not { } branchEntries || branchEntries.Count is < 2 or > 8
                    || branchEntries.Distinct(StringComparer.Ordinal).Count() != branchEntries.Count)
                { Error(node.Id, "Parallel requires 2–8 distinct branch entries."); continue; }
                if (!ValidTarget(node.NextStepId)) Error(node.Id, "Parallel Next is required.");
                for (var i = 0; i < branchEntries.Count; i++)
                {
                    if (!ValidTarget(branchEntries[i])) Error(node.Id, $"Parallel branch {i + 1} is required.");
                    else links.Add(new(node.Id, branchEntries[i], $"branch:{i}"));
                }
            }
            if (isTrigger && (node.Trigger is null || !ValidTarget(node.NextStepId))) Error(node.Id, "Trigger settings and Next are required.");
        }
        if (errors.Count > 0) return new(errors);
        WorkflowDefinitionValidator.ValidateTriggers(nodes.Values.Where(n => n.Trigger is not null).Select(n =>
            new WorkflowDefinitionTrigger(n.Id, n.Trigger!.Type, n.Trigger.Enabled, n.Trigger.RepositoryIds,
                n.Trigger.Label, n.Trigger.BaseBranch, n.Trigger.Model, n.NextStepId)).ToArray(), errors);

        // Record exact allowed incoming edges for each body task, preventing entry into or overlap of regions.
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        var allowedIncoming = new Dictionary<string, Link>(StringComparer.Ordinal);
        var terminals = new Dictionary<string, (string Group, string Port)>(StringComparer.Ordinal);
        void Collect(string start, WorkflowDefinitionStep group, string entryRole, string returnPort, bool planningOnly)
        {
            var cursor = start;
            var previous = group.Id;
            var role = entryRole;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (nodes.TryGetValue(cursor, out var task) && seen.Add(cursor))
            {
                if (!WorkflowDefinitionValidator.TryMapUsesToTaskKind(task.Uses, out var kind) || (planningOnly && kind != TaskRunKind.Plan)) break;
                if (!owner.TryAdd(cursor, group.Id)) Error(cursor, "Control bodies cannot overlap or share tasks.");
                allowedIncoming.TryAdd(cursor, new(previous, cursor, role));
                if (task.NextStepId == group.Id && task.NextStepPort == returnPort)
                {
                    terminals[cursor] = (group.Id, returnPort);
                    return;
                }
                if (task.NextStepId is null || task.NextStepPort is not null) break;
                previous = cursor; role = "next"; cursor = task.NextStepId;
            }
            Error(group.Id, planningOnly
                ? "Each Parallel branch must be a disjoint Plan chain ending at its Join. Nested controls are unsupported."
                : "Loop Body must be a sequential task chain ending at Return. Decisions and nested controls are unsupported inside loops.");
        }
        foreach (var group in nodes.Values)
        {
            if (group.Loop is { } loop) Collect(loop.BodyStepId, group, "body", "return", false);
            if (group.Parallel is { } parallel)
                for (var i = 0; i < parallel.BranchStepIds.Count; i++) Collect(parallel.BranchStepIds[i], group, $"branch:{i}", "join", true);
        }
        foreach (var link in links)
        {
            if (owner.ContainsKey(link.Target) && allowedIncoming[link.Target] != link)
                Error(link.Source, "Enter a Loop or Parallel region through its control node, not a body task.");
            if ((link.Role is "return" or "join")
                && (!terminals.TryGetValue(link.Source, out var terminal) || terminal.Group != link.Target || terminal.Port != link.Role))
                Error(link.Source, "Only the last task of a region may connect to its own Return or Join input.");
        }
        if (owner.ContainsKey(document.StartStepId)) Error(document.StartStepId, "Manual start cannot enter a control body.");
        foreach (var group in nodes.Values.Where(n => n.Loop is not null || n.Parallel is not null))
            if (group.NextStepId == group.Id || owner.ContainsKey(group.NextStepId!)) Error(group.Id, "Control continuation must leave all control bodies.");
        if (errors.Count > 0) return new(errors);

        var outer = nodes.Keys.Where(id => !owner.ContainsKey(id)).ToHashSet(StringComparer.Ordinal);
        var outerLinks = links.Where(link => outer.Contains(link.Source) && outer.Contains(link.Target)).ToArray();
        var outgoing = outer.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        var incoming = outer.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var link in outerLinks) { outgoing[link.Source].Add(link.Target); incoming[link.Target].Add(link.Source); }
        var entries = nodes.Values.Where(n => n.Uses == WorkflowNodeDefinitions.TriggerUses).Select(n => n.Id)
            .Append(document.StartStepId).ToHashSet(StringComparer.Ordinal);
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(entries);
        while (pending.TryPop(out var id))
            if (reached.Add(id)) foreach (var target in outgoing[id]) pending.Push(target);
        foreach (var id in outer.Except(reached)) Error(id, "Node is not reachable from a manual or trigger entry.");
        var indegree = incoming.ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal);
        var ready = new Queue<string>(outer.Where(id => indegree[id] == 0));
        var order = new List<string>();
        while (ready.TryDequeue(out var id))
        {
            order.Add(id);
            foreach (var target in outgoing[id]) if (--indegree[target] == 0) ready.Enqueue(target);
        }
        foreach (var id in outer.Except(order)) Error(id, "Execution paths outside Loop Return and Parallel Join must be acyclic.");
        if (errors.Count > 0) return new(errors);

        var dominators = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var id in order)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (!entries.Contains(id) && incoming[id].Count > 0)
            {
                set.UnionWith(dominators[incoming[id][0]]);
                foreach (var predecessor in incoming[id].Skip(1)) set.IntersectWith(dominators[predecessor]);
            }
            set.Add(id); dominators[id] = set;
        }
        foreach (var node in nodes.Values.Where(n => n.Decision?.Condition.Source == "taskOutput"))
        {
            var reference = node.Decision!.Condition.Reference;
            if (reference is null || !nodes.TryGetValue(reference, out var source)
                || !WorkflowDefinitionValidator.TryMapUsesToTaskKind(source.Uses, out _)
                || owner.ContainsKey(reference) || !dominators[node.Id].Contains(reference))
                Error(node.Id, "Task output must reference an ordinary task guaranteed to finish on every path to this Decision. Loop and Parallel body outputs are not supported.");
        }
        return new(errors);
    }
}
