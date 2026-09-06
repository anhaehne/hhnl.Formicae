namespace hhnl.Formicae.Application.Workflows;

/// <summary>Compiles persisted control nodes into the existing task/iteration execution plan.</summary>
public static class WorkflowNodeDefinitions
{
    public const string TriggerUses = "builtins.trigger";
    public const string LoopUses = "builtins.loop";

    public static WorkflowDefinitionValidationResult Validate(WorkflowDefinitionDocument document)
    {
        if (document.Steps.Any(n => n.Uses == WorkflowDecisionDefinitions.Uses || n.Decision is not null)) return WorkflowDecisionDefinitions.Validate(document);
        if (document.Steps.Any(n => n.Uses == WorkflowParallelDefinitions.Uses || n.Parallel is not null)) return WorkflowParallelDefinitions.Validate(document);
        var errors = new List<WorkflowDefinitionValidationError>();
        void Error(string message, string path = "steps", string? nodeId = null) => errors.Add(new("definition.node.invalid", message, path, nodeId));
        if (document.Triggers?.Count > 0 || document.Loops?.Count > 0) Error("v1alpha3 stores triggers and loops on nodes, not in top-level lists.");
        if (document.Steps.Count == 0) Error("At least one task node is required.");
        if (document.Steps.Any(n => string.IsNullOrWhiteSpace(n.Id)) || document.Steps.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() != document.Steps.Count)
            Error("Node IDs must be nonempty and unique.");
        if (errors.Count > 0) return new(errors);
        var nodes = document.Steps.ToDictionary(n => n.Id, StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(document.StartStepId) || !nodes.TryGetValue(document.StartStepId, out var start) || start.Uses == TriggerUses) Error("Manual start must reference a task or loop node.", "startStepId");
        foreach (var node in nodes.Values)
        {
            if (node.NextStepId is { } next && (!nodes.ContainsKey(next) || nodes[next].Uses == TriggerUses)) Error($"Node '{node.Id}' has an invalid next connection.", nodeId: node.Id);
            if (node.NextStepPort is not (null or "return")) Error($"Node '{node.Id}' has an unknown target port.", nodeId: node.Id);
            if (node.Uses is TriggerUses or LoopUses)
            {
                if (node.NextStepId is null) Error($"Control node '{node.Id}' needs an outgoing connection.", nodeId: node.Id);
                if (node.NextStepPort is not null) Error($"Control node '{node.Id}' cannot connect to a Return input.", nodeId: node.Id);
                if (node.AiSettingsId is not null || node.Model is not null) Error($"Control node '{node.Id}' cannot have agent settings.", nodeId: node.Id);
                if (node.Uses == TriggerUses && (node.Trigger is null || node.Loop is not null)) Error($"Trigger '{node.Id}' needs trigger settings only.", nodeId: node.Id);
                if (node.Uses == LoopUses && (node.Loop is null || node.Trigger is not null)) Error($"Loop '{node.Id}' needs loop settings only.", nodeId: node.Id);
            }
            else if (string.IsNullOrWhiteSpace(node.Uses) || !WorkflowDefinitionValidator.TryMapUsesToTaskKind(node.Uses, out _) || node.Trigger is not null || node.Loop is not null)
                Error($"Task '{node.Id}' has an unsupported type or control settings.", nodeId: node.Id);
        }
        if (errors.Count > 0) return new(errors);
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        var bodies = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var loop in nodes.Values.Where(n => n.Uses == LoopUses))
        {
            var body = new List<string>();
            var cursor = loop.Loop!.BodyStepId ?? "";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (nodes.TryGetValue(cursor, out var task) && seen.Add(cursor) && WorkflowDefinitionValidator.TryMapUsesToTaskKind(task.Uses, out _))
            {
                body.Add(cursor);
                if (!owner.TryAdd(cursor, loop.Id)) Error($"Loop '{loop.Id}' overlaps another loop.", nodeId: loop.Id);
                if (task.NextStepId == loop.Id && task.NextStepPort == "return") break;
                if (task.NextStepId is null || task.NextStepPort is not null) break;
                cursor = task.NextStepId;
            }
            if (body.Count == 0 || nodes[body[^1]].NextStepId != loop.Id || nodes[body[^1]].NextStepPort != "return")
                Error($"Loop '{loop.Id}' needs a nonempty, sequential task body ending at its Return input. Nested loops are not supported.", nodeId: loop.Id);
            if (loop.NextStepId == loop.Id || body.Contains(loop.NextStepId!)) Error($"Loop '{loop.Id}' must exit outside its body.", nodeId: loop.Id);
            bodies[loop.Id] = body;
        }
        foreach (var node in nodes.Values)
        {
            if (node.NextStepPort == "return" && (node.NextStepId is null || !bodies.TryGetValue(node.NextStepId, out var returnBody) || returnBody.LastOrDefault() != node.Id))
                Error($"Only the last body task can connect to a loop's Return input ('{node.Id}').", nodeId: node.Id);
            if (node.NextStepId is { } target && owner.TryGetValue(target, out var loopId))
            {
                var body = bodies[loopId];
                var index = body.IndexOf(target);
                if (index == 0 || body[index - 1] != node.Id) Error($"Enter loop '{loopId}' through its node, not through a body task.", nodeId: node.Id);
            }
        }
        if (owner.ContainsKey(document.StartStepId)) Error("Manual start cannot enter a loop body directly.", "startStepId");
        if (errors.Count > 0) return new(errors);

        // Check reachability using all event entries, not just the manual entry.
        var reached = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (!reached.Add(id)) return;
            var node = nodes[id];
            if (node.NextStepId is not null) Visit(node.NextStepId);
            if (node.Loop is not null) Visit(node.Loop.BodyStepId);
        }
        Visit(document.StartStepId);
        foreach (var trigger in nodes.Values.Where(n => n.Uses == TriggerUses)) Visit(trigger.Id);
        foreach (var id in nodes.Keys.Except(reached)) Error($"Node '{id}' is not reachable from a manual or trigger entry.", nodeId: id);
        if (errors.Count > 0) return new(errors);

        WorkflowDefinitionDocument plan;
        try { plan = Normalize(document); }
        catch (InvalidOperationException exception) { Error(exception.Message); return new(errors); }
        var entries = new[] { plan.StartStepId }.Concat(plan.Triggers?.Select(t => t.NextStepId!) ?? []).Distinct();
        foreach (var entry in entries)
        {
            var validation = new WorkflowDefinitionValidator().Validate(plan with { StartStepId = entry });
            errors.AddRange(validation.Errors.Where(e => e.Code is not ("definition.graph.disconnected" or "definition.graph.terminal.invalid")));
        }
        return new(errors.Distinct().ToArray());
    }

    public static WorkflowDefinitionDocument Normalize(WorkflowDefinitionDocument document)
    {
        if (document.Schema != DefaultWorkflowDefinitions.V1Alpha3Schema) return document;
        var nodes = document.Steps.ToDictionary(n => n.Id, StringComparer.Ordinal);
        string Entry(string id)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (nodes[id].Uses == LoopUses)
            {
                if (!seen.Add(id)) throw new InvalidOperationException("Loop entry connections contain a cycle.");
                id = nodes[id].Loop!.BodyStepId;
            }
            return id;
        }
        var loops = nodes.Values.Where(n => n.Uses == LoopUses).Select(node =>
        {
            var body = new List<string>();
            var cursor = node.Loop!.BodyStepId;
            while (cursor != node.Id)
            {
                if (body.Contains(cursor) || !nodes.TryGetValue(cursor, out var task)) throw new InvalidOperationException("Invalid loop body.");
                body.Add(cursor);
                cursor = task.NextStepId ?? throw new InvalidOperationException("Loop body has no Return connection.");
            }
            return new WorkflowDefinitionLoop(node.Id, body, node.Loop.RepeatCount, node.Loop.MaxIterations, Entry(node.NextStepId!), node.Loop.TimeoutSeconds);
        }).ToArray();
        var tasks = nodes.Values.Where(n => n.Uses is not (LoopUses or TriggerUses)).Select(n => n with
        {
            NextStepId = n.NextStepId is null ? null : Entry(n.NextStepId), NextStepPort = n.NextStepPort == "join" ? "join" : null,
            Decision = n.Decision is null ? null : n.Decision with
            {
                ConfiguredTrueStepId = n.Decision.ConfiguredTrueStepId ?? n.Decision.TrueStepId,
                ConfiguredFalseStepId = n.Decision.ConfiguredFalseStepId ?? n.Decision.FalseStepId,
                TrueStepId = Entry(n.Decision.TrueStepId), FalseStepId = Entry(n.Decision.FalseStepId)
            }
        }).ToArray();
        var triggers = nodes.Values.Where(n => n.Uses == TriggerUses).Select(n => new WorkflowDefinitionTrigger(
            n.Id, n.Trigger!.Type, n.Trigger.Enabled, n.Trigger.RepositoryIds, n.Trigger.Label, n.Trigger.BaseBranch, n.Trigger.Model, Entry(n.NextStepId!))).ToArray();
        return new(DefaultWorkflowDefinitions.V1Alpha2Schema, Entry(document.StartStepId), tasks, triggers, loops);
    }
}
