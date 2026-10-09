using System.Text.Json;

namespace hhnl.Formicae.Application.Workflows;

public sealed record WorkflowDataVariable(string Id, string Name, string ValueType,
    string Mode = "aggregate", IReadOnlyList<CustomTaskInputBinding>? Sources = null,
    string Separator = "\n", string BooleanOperation = "any");
public sealed record WorkflowVariableEvidence(WorkflowDataVariable Configuration, IReadOnlyList<CustomTaskInputProvenance> Sources);

/// <summary>Pure data expressions. They never enter the control graph or create task runs.</summary>
public static class WorkflowVariableDefinitions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<WorkflowDefinitionValidationError> Validate(WorkflowDefinitionDocument document, bool validateTypes = true)
    {
        var errors = new List<WorkflowDefinitionValidationError>();
        var variables = document.Variables ?? [];
        void Error(string message, string? id = null) => errors.Add(new("definition.variable.invalid", message, "variables", id));
        var ids = document.Steps.Select(step => step.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var variable in variables)
        {
            if (variable is null) { Error("Each variable must be an object."); continue; }
            if (string.IsNullOrWhiteSpace(variable.Id) || !ids.Add(variable.Id)) Error("Variable IDs must be nonempty and unique across tasks and variables.", variable.Id);
            if (string.IsNullOrWhiteSpace(variable.Name) || variable.Name.Length > 120) Error("Variable name is required and may contain at most 120 characters.", variable.Id);
            if (variable.ValueType is not ("string" or "number" or "boolean")) Error("Variable type must be string, number or boolean.", variable.Id);
            if (variable.Mode is not ("aggregate" or "first" or "override")) Error("Variable mode must be Aggregate, First or Override.", variable.Id);
            if (variable.BooleanOperation is not ("any" or "all")) Error("Boolean operation must be Any or All.", variable.Id);
            if (variable.Separator is null || variable.Separator.Length > 16000) Error("Separator must be a string of at most 16000 characters.", variable.Id);
            var seen = new HashSet<CustomTaskInputBinding>();
            foreach (var source in variable.Sources ?? [])
                if (source is null || string.IsNullOrWhiteSpace(source.StepId) || string.IsNullOrWhiteSpace(source.OutputName) || !seen.Add(source))
                    Error("Variable sources require unique producer/output pairs.", variable.Id);
        }
        if (errors.Count > 0) return errors;
        var byId = variables.ToDictionary(variable => variable.Id, StringComparer.Ordinal);
        var visiting = new HashSet<string>(); var visited = new HashSet<string>();
        void Visit(WorkflowDataVariable variable)
        {
            if (visiting.Contains(variable.Id)) { Error("Variable connections cannot contain cycles.", variable.Id); return; }
            if (!visited.Add(variable.Id)) return;
            visiting.Add(variable.Id);
            foreach (var source in variable.Sources ?? [])
            {
                string? type;
                if (byId.TryGetValue(source.StepId, out var parent))
                {
                    type = source.OutputName == "value" ? parent.ValueType : null;
                    Visit(parent);
                }
                else type = document.Steps.FirstOrDefault(step => step.Id == source.StepId) is { } step
                    ? CustomTaskDefinitions.OutputSchemaFor(step).FirstOrDefault(output => output.Name == source.OutputName)?.ValueType : null;
                if (validateTypes && type != variable.ValueType) Error("Every source must declare an output with the same type as its variable.", variable.Id);
            }
            visiting.Remove(variable.Id);
        }
        foreach (var variable in variables) Visit(variable);
        foreach (var step in document.Steps)
        foreach (var (name, source) in CustomTaskDefinitions.BindingsFor(step))
            if (validateTypes && source is not null && byId.TryGetValue(source.StepId, out var variable)
                && (source.OutputName != "value" || CustomTaskDefinitions.InputSchemaFor(step).FirstOrDefault(input => input.Name == name)?.ValueType != variable.ValueType))
                Error($"Input '{name}' must match the connected variable type.", step.Id);
        return errors;
    }

    // Keep control ancestry checks on all leaf producers, including values ignored by First/Override.
    public static IEnumerable<CustomTaskInputBinding> Expand(WorkflowDefinitionDocument document, CustomTaskInputBinding binding)
    {
        var variable = document.Variables?.FirstOrDefault(item => item.Id == binding?.StepId);
        if (variable is null) { yield return binding; yield break; }
        foreach (var source in variable.Sources ?? [])
        foreach (var leaf in Expand(document, source)) yield return leaf;
    }

    public static JsonElement? Combine(WorkflowDataVariable variable, IEnumerable<JsonElement?> sources)
    {
        var values = sources.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        if (values.Length == 0) return null;
        if (values.Any(value => !CustomTaskDefinitions.ValidScalar(value, variable.ValueType)))
            throw new InvalidOperationException($"Variable '{variable.Name}' requires bounded {variable.ValueType} values.");
        var result = variable.Mode switch
        {
            "first" => values[0].Clone(),
            "override" => values[^1].Clone(),
            "aggregate" => variable.ValueType switch
            {
                "string" => JsonSerializer.SerializeToElement(string.Join(variable.Separator, values.Select(value => value.GetString()))),
                "number" => JsonSerializer.SerializeToElement(values.Aggregate(0m, (sum, value) => checked(sum + value.GetDecimal()))),
                "boolean" when variable.BooleanOperation == "any" => JsonSerializer.SerializeToElement(values.Any(value => value.GetBoolean())),
                "boolean" when variable.BooleanOperation == "all" => JsonSerializer.SerializeToElement(values.All(value => value.GetBoolean())),
                _ => throw new InvalidOperationException("Invalid variable aggregation operation.")
            },
            _ => throw new InvalidOperationException("Invalid variable mode.")
        };
        if (!CustomTaskDefinitions.ValidScalar(result, variable.ValueType))
            throw new InvalidOperationException($"Variable '{variable.Name}' result exceeds its {variable.ValueType} limits.");
        return result;
    }

    public static async Task<CustomTaskInputProvenance> ResolveAsync(WorkflowDefinitionDocument document, CustomTaskInputBinding binding,
        Func<CustomTaskInputBinding, Task<CustomTaskInputProvenance>> load)
    {
        var visiting = new HashSet<string>();
        async Task<CustomTaskInputProvenance> Resolve(CustomTaskInputBinding source)
        {
            var variable = document.Variables?.FirstOrDefault(item => item.Id == source.StepId);
            if (variable is null) return await load(source);
            if (source.OutputName != "value" || !visiting.Add(variable.Id)) throw new InvalidOperationException("Invalid or cyclic variable binding.");
            var evidence = new List<CustomTaskInputProvenance>();
            foreach (var input in variable.Sources ?? []) evidence.Add(await Resolve(input));
            visiting.Remove(variable.Id);
            return new(variable.Id, "value", Guid.Empty, Guid.Empty, null, Combine(variable, evidence.Select(item => item.Value)), new(variable, evidence));
        }
        return await Resolve(binding);
    }

    public static void ValidateEvidence(CustomTaskInputProvenance source)
    {
        if (source.Variable is not { } variable)
        {
            if (source.RunId == Guid.Empty || source.ExecutionAttemptId == Guid.Empty)
                throw new InvalidOperationException("Binding has no valid frozen producer identity.");
            return;
        }
        if (source.StepId != variable.Configuration.Id || source.OutputName != "value" || variable.Sources is null
            || variable.Sources.Count != (variable.Configuration.Sources?.Count ?? 0))
            throw new InvalidOperationException("Frozen variable evidence does not match its sources.");
        for (var i = 0; i < variable.Sources.Count; i++)
        {
            var expected = variable.Configuration.Sources![i]; var actual = variable.Sources[i];
            if (actual.StepId != expected.StepId || actual.OutputName != expected.OutputName)
                throw new InvalidOperationException("Frozen variable source order does not match its configuration.");
            ValidateEvidence(actual);
        }
        var result = Combine(variable.Configuration, variable.Sources.Select(item => item.Value));
        if (JsonSerializer.Serialize(result, Json) != JsonSerializer.Serialize(source.Value, Json))
            throw new InvalidOperationException("Frozen variable value does not match its source evidence.");
    }

    public static void ValidatePinnedEvidence(IEnumerable<CustomTaskInputProvenance> sources, WorkflowDefinitionDocument document, int? iteration)
    {
        foreach (var source in sources)
        {
            ValidateEvidence(source);
            if (source.Variable is { } variable)
            {
                var pinned = document.Variables?.FirstOrDefault(item => item.Id == source.StepId);
                if (pinned is null || JsonSerializer.Serialize(pinned, Json) != JsonSerializer.Serialize(variable.Configuration, Json))
                    throw new InvalidOperationException("Prepared variable configuration does not match the pinned definition.");
                ValidatePinnedEvidence(variable.Sources, document, iteration);
            }
            else
            {
                if (document.Variables?.Any(item => item.Id == source.StepId) == true)
                    throw new InvalidOperationException("Variable binding is missing its frozen source evidence.");
                var loop = document.Loops?.FirstOrDefault(item => item.BodyStepIds.Contains(source.StepId));
                if (source.LoopIteration != (loop is null ? null : iteration))
                    throw new InvalidOperationException("Prepared binding provenance does not match this loop iteration.");
            }
        }
    }
}
