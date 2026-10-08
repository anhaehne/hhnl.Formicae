namespace hhnl.Formicae.Application.Workflows;

/// <summary>Ordinary task connections form an acyclic dependency graph with all-input joins.</summary>
public static class WorkflowGraphDefinitions
{
    public static bool IsGraph(WorkflowDefinitionDocument document) => document.Steps.Any(step => step.NextStepIds is { Count: > 0 });

    public static IReadOnlyList<string> Successors(WorkflowDefinitionStep step)
        => (step.NextStepId is null ? Enumerable.Empty<string>() : [step.NextStepId])
            .Concat(step.NextStepIds ?? []).ToArray();

    public static HashSet<string> Reachable(WorkflowDefinitionDocument document, string entry)
    {
        var nodes = document.Steps.ToDictionary(step => step.Id, StringComparer.Ordinal);
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(entry);
        while (pending.TryPop(out var id))
            if (reached.Add(id) && nodes.TryGetValue(id, out var step))
                foreach (var next in Successors(step)) pending.Push(next);
        return reached;
    }

    public static WorkflowDefinitionValidationResult Validate(WorkflowDefinitionDocument document, bool allowAlternativeEntries = false)
    {
        var errors = new List<WorkflowDefinitionValidationError>();
        void Error(string message, string? id = null) => errors.Add(new("definition.graph.invalid", message, "steps", id));
        if (document.Steps.Count == 0 || document.Steps.Any(step => string.IsNullOrWhiteSpace(step.Id))
            || document.Steps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() != document.Steps.Count)
            return new([new("definition.graph.invalid", "Task IDs must be nonempty and unique.", "steps")]);
        var nodes = document.Steps.ToDictionary(step => step.Id, StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(document.StartStepId) || !nodes.TryGetValue(document.StartStepId, out var start) || start.Uses == WorkflowNodeDefinitions.TriggerUses)
            Error("Manual start must reference a task.");
        if (document.Loops?.Count > 0 || document.Triggers?.Count > 0) Error("Graph workflows store triggers on nodes.");
        foreach (var step in document.Steps)
        {
            var next = Successors(step);
            if (next.Any(string.IsNullOrWhiteSpace) || next.Distinct(StringComparer.Ordinal).Count() != next.Count)
                Error("Outgoing connections must reference distinct, nonempty task IDs.", step.Id);
            foreach (var id in next)
                if (string.IsNullOrWhiteSpace(id) || !nodes.TryGetValue(id, out var target) || target.Uses == WorkflowNodeDefinitions.TriggerUses)
                    Error($"Outgoing connection '{id}' must reference a task.", step.Id);
            if (step.Uses == WorkflowNodeDefinitions.TriggerUses)
            {
                if (step.Trigger is null || next.Count != 1 || step.AiSettingsId is not null || step.Model is not null)
                    Error("Trigger nodes require settings and one task entry connection.", step.Id);
            }
            else if (string.IsNullOrWhiteSpace(step.Uses) || !WorkflowDefinitionValidator.TryMapUsesToTaskKind(step.Uses, out _) || step.Trigger is not null)
                Error("Multiple task connections currently support task and trigger nodes; use explicit control nodes in a separate workflow for loops and decisions.", step.Id);
            if (step.NextStepPort is not null || step.Loop is not null || step.Parallel is not null || step.Decision is not null)
                Error("Task graph connections must use ordinary input ports.", step.Id);
        }
        if (errors.Count > 0) return new(errors);
        var triggerSettings = document.Steps.Where(step => step.Trigger is not null).Select(step => new WorkflowDefinitionTrigger(
            step.Id, step.Trigger!.Type, step.Trigger.Enabled, step.Trigger.RepositoryIds, step.Trigger.Label,
            step.Trigger.BaseBranch, step.Trigger.Model, step.NextStepId)).ToArray();
        WorkflowDefinitionValidator.ValidateTriggers(triggerSettings, errors);
        var indegree = nodes.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var step in nodes.Values) foreach (var id in Successors(step)) indegree[id]++;
        var ready = new Queue<string>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;
        while (ready.TryDequeue(out var id))
        {
            visited++;
            foreach (var next in Successors(nodes[id])) if (--indegree[next] == 0) ready.Enqueue(next);
        }
        if (visited != nodes.Count) Error("Task connections contain a cycle. Parallel joins require an acyclic graph.");
        var reached = Reachable(document, document.StartStepId);
        foreach (var trigger in nodes.Values.Where(step => step.Uses == WorkflowNodeDefinitions.TriggerUses))
            reached.UnionWith(Reachable(document, trigger.Id));
        foreach (var id in nodes.Keys.Except(reached)) Error("Task is not reachable from a workflow entry.", id);
        // A join cannot wait on a task that this execution entry will never activate.
        foreach (var entry in allowAlternativeEntries ? Enumerable.Empty<string>() : new[] { document.StartStepId }.Concat(nodes.Values.Where(step => step.Trigger is not null).Select(step => step.NextStepId!)))
        {
            var active = Reachable(document, entry);
            foreach (var step in nodes.Values.Where(step => step.Uses != WorkflowNodeDefinitions.TriggerUses && !active.Contains(step.Id)))
                foreach (var next in Successors(step).Where(active.Contains))
                    Error($"Join '{next}' depends on '{step.Id}', which is unreachable from entry '{entry}'.", next);
        }
        return new(errors);
    }
}
