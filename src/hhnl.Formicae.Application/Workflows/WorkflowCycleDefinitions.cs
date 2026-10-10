using System.Text.Json;

namespace hhnl.Formicae.Application.Workflows;

/// <summary>Control feedback is execution, not recursive evaluation of data expressions.</summary>
public static class WorkflowCycleDefinitions
{
    public static IEnumerable<string> Successors(WorkflowDefinitionDocument document, WorkflowDefinitionStep step)
    {
        var loop = document.Loops?.FirstOrDefault(item => item.BodyStepIds.LastOrDefault() == step.Id);
        if (loop is not null) return [loop.ExitStepId];
        return step.Decision is { } decision ? WorkflowDecisionDefinitions.Routes(decision).Select(route => route.Target)
            : WorkflowGraphDefinitions.Successors(step);
    }

    public static Dictionary<string, int> Components(WorkflowDefinitionDocument document)
    {
        var nodes = document.Steps.ToDictionary(step => step.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<string>(); var stacked = new HashSet<string>();
        var index = 0; var component = 0;
        void Visit(string id)
        {
            indices[id] = low[id] = index++; stack.Push(id); stacked.Add(id);
            foreach (var target in Successors(document, nodes[id]).Where(nodes.ContainsKey))
            {
                if (!indices.ContainsKey(target)) { Visit(target); low[id] = Math.Min(low[id], low[target]); }
                else if (stacked.Contains(target)) low[id] = Math.Min(low[id], indices[target]);
            }
            if (low[id] != indices[id]) return;
            string item;
            do { item = stack.Pop(); stacked.Remove(item); result[item] = component; } while (item != id);
            component++;
        }
        foreach (var id in nodes.Keys) if (!indices.ContainsKey(id)) Visit(id);
        return result;
    }

    public static bool HasCycles(WorkflowDefinitionDocument document)
    {
        var components = Components(document);
        return components.Values.GroupBy(id => id).Any(group => group.Count() > 1)
            || document.Steps.Any(step => Successors(document, step).Contains(step.Id));
    }

    public static bool IsFeedback(WorkflowDefinitionDocument document, string producer, string consumer)
    {
        var components = Components(document);
        return components.TryGetValue(producer, out var a) && components.TryGetValue(consumer, out var b) && a == b
            && (producer != consumer || document.Steps.Any(step => step.Id == producer && Successors(document, step).Contains(producer))
                || components.Count(item => item.Value == a) > 1);
    }

    public static WorkflowCycleState State(Workflow workflow) => workflow.CycleExecutionJson is null ? new()
        : JsonSerializer.Deserialize<WorkflowCycleState>(workflow.CycleExecutionJson) ?? throw new InvalidOperationException("Cycle execution state is missing.");
    public static void Save(Workflow workflow, WorkflowCycleState state) => workflow.CycleExecutionJson = JsonSerializer.Serialize(state);
    public static int? Visit(Workflow workflow, string id) => State(workflow).Active.GetValueOrDefault(id)?.Visit;
}

public sealed class WorkflowCycleState
{
    public string? Entry { get; set; }
    public string? EntryPlanArtifact { get; set; }
    public Dictionary<string, int> Completed { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, WorkflowCycleActivation> Active { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Delivered { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Consumed { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, WorkflowCycleLoop> Loops { get; set; } = new(StringComparer.Ordinal);
}
public sealed record WorkflowCycleActivation(int Visit, Dictionary<string, int> Sources, DateTimeOffset StartedAt);
public sealed record WorkflowCycleLoop(int Iteration, DateTimeOffset StartedAt);
