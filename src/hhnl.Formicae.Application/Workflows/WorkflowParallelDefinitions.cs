namespace hhnl.Formicae.Application.Workflows;

/// <summary>Validates bounded, disjoint planning branches and their explicit join.</summary>
public static class WorkflowParallelDefinitions
{
    public const string Uses = "builtins.parallel";

    public static IReadOnlyList<IReadOnlyList<WorkflowDefinitionStep>> Branches(WorkflowDefinitionDocument document, WorkflowDefinitionStep group)
    {
        var nodes = document.Steps.ToDictionary(n => n.Id, StringComparer.Ordinal);
        return group.Parallel!.BranchStepIds.Select(entry =>
        {
            var branch = new List<WorkflowDefinitionStep>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var id = entry;
            while (nodes.TryGetValue(id, out var node) && seen.Add(id))
            {
                branch.Add(node);
                if (node.NextStepId == group.Id && node.NextStepPort == "join") return (IReadOnlyList<WorkflowDefinitionStep>)branch;
                id = node.NextStepId ?? "";
            }
            throw new InvalidOperationException($"Parallel branch '{entry}' does not end at '{group.Id}' Join.");
        }).ToArray();
    }

    public static WorkflowDefinitionValidationResult Validate(WorkflowDefinitionDocument document)
    {
        var errors = new List<WorkflowDefinitionValidationError>();
        void Error(string id, string message) => errors.Add(new("definition.parallel.invalid", message, "steps", id));
        if (document.Steps.Any(n => string.IsNullOrWhiteSpace(n.Id)) || document.Steps.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() != document.Steps.Count)
            return new([new("definition.parallel.invalid", "Node IDs must be nonempty and unique.", "steps")]);
        var nodes = document.Steps.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var groups = new Dictionary<string, IReadOnlyList<IReadOnlyList<WorkflowDefinitionStep>>>(StringComparer.Ordinal);
        foreach (var node in nodes.Values)
        {
            if (node.Uses != Uses)
            {
                if (node.Parallel is not null) Error(node.Id, "Only Parallel nodes can have parallel settings.");
                continue;
            }
            if (node.Trigger is not null || node.Loop is not null || node.AiSettingsId is not null || node.Model is not null || node.NextStepPort is not null)
                Error(node.Id, "Parallel nodes cannot have task, loop, trigger or return settings.");
            var entries = node.Parallel?.BranchStepIds;
            if (entries is null || entries.Count is < 2 or > 8 || entries.Any(string.IsNullOrWhiteSpace) || entries.Distinct(StringComparer.Ordinal).Count() != entries.Count)
            { Error(node.Id, "Parallel requires 2–8 distinct branch entries."); continue; }
            if (node.NextStepId is null || !nodes.ContainsKey(node.NextStepId) || node.NextStepId == node.Id)
                Error(node.Id, "Parallel requires a Next connection outside its branches.");
            IReadOnlyList<IReadOnlyList<WorkflowDefinitionStep>> branches;
            try { branches = Branches(document, node); }
            catch (InvalidOperationException exception) { Error(node.Id, exception.Message); continue; }
            groups[node.Id] = branches;
            foreach (var branch in branches)
            foreach (var task in branch)
            {
                if (task.Uses != "builtins.plan") Error(task.Id, "Parallel branches support Plan tasks only. Tasks that modify the shared Git branch must run sequentially.");
                if (!owners.TryAdd(task.Id, node.Id)) Error(task.Id, "Parallel branches cannot overlap or share tasks.");
                if (task.NextStepId == node.NextStepId) Error(node.Id, "Parallel Next must be outside its branches.");
            }
        }
        if (errors.Count > 0) return new(errors);
        foreach (var group in groups)
        {
            var fork = nodes[group.Key];
            if (owners.ContainsKey(fork.NextStepId!)) Error(fork.Id, "Parallel Next cannot enter a branch body.");
            foreach (var branch in group.Value)
            for (var i = 0; i < branch.Count; i++)
            {
                var task = branch[i];
                if (task.Id == document.StartStepId) Error(task.Id, "Manual start cannot enter a parallel branch.");
                foreach (var incoming in nodes.Values.Where(n => n.NextStepId == task.Id))
                    if (i == 0 || incoming.Id != branch[i - 1].Id) Error(incoming.Id, "Enter parallel branches through their Parallel node.");
            }
        }
        foreach (var node in nodes.Values)
        {
            if (node.NextStepPort == "join" && (!owners.TryGetValue(node.Id, out var owner) || node.NextStepId != owner)) Error(node.Id, "Join connections must return to the owning Parallel node.");
            if (node.Loop is not null)
            {
                var seen = new HashSet<string>(); var cursor = node.Loop.BodyStepId;
                while (cursor is not null && nodes.TryGetValue(cursor, out var body) && seen.Add(cursor) && cursor != node.Id)
                {
                    if (body.Uses == Uses || owners.ContainsKey(cursor)) Error(node.Id, "Parallel groups cannot be nested in loops.");
                    cursor = body.NextStepId;
                }
            }
        }
        if (errors.Count > 0) return new(errors);
        // Validate all existing graph and loop invariants using a serial expansion of each region.
        var flattened = nodes.ToDictionary(n => n.Key, n => n.Value, StringComparer.Ordinal);
        foreach (var (id, branches) in groups)
        {
            flattened[id] = nodes[id] with { Uses = "builtins.plan", Parallel = null, NextStepId = branches[0][0].Id };
            for (var i = 0; i < branches.Count; i++)
            {
                var last = branches[i][^1];
                flattened[last.Id] = last with { NextStepId = i + 1 < branches.Count ? branches[i + 1][0].Id : nodes[id].NextStepId, NextStepPort = null };
            }
        }
        return WorkflowNodeDefinitions.Validate(document with { Steps = flattened.Values.ToArray() });
    }
}
