using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace hhnl.Formicae.Application.Workflows;

public static class CustomTaskDefinitions
{
    public const string Uses = "builtins.custom-task";
    public const int MaximumPromptBytes = 131072;
    private const int MaximumInputBytes = 65536;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> WorkflowNames = ["issueUrl", "repositoryUrl", "baseBranch", "model", "planArtifact", "pullRequestUrl"];
    private sealed record Part(string Text, string? Source = null, string? Name = null);

    public static WorkflowDefinitionValidationResult ValidateCatalog(string? name, string? description, string? promptTemplate,
        IReadOnlyList<CustomTaskInputDefinition>? inputs, CustomTaskRunnerSettings? runner)
    {
        var errors = new List<WorkflowDefinitionValidationError>();
        void Error(string message, string path) => errors.Add(new("definition.customTask.invalid", message, path));
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120) Error("Task name is required and must contain at most 120 characters.", "name");
        if (description?.Length > 2000) Error("Description must contain at most 2000 characters.", "description");
        if (string.IsNullOrWhiteSpace(promptTemplate) || promptTemplate.Length > 16000) Error("Prompt template is required and must contain at most 16000 characters.", "promptTemplate");
        if (runner is null || runner.Kind != "agent" || runner.TimeoutSeconds is < 1 or > 3600)
            Error("An agent runner with a timeout between 1 and 3600 seconds is required.", "runner");
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (inputs is null || inputs.Count > 32) Error("Input schema is required and may contain at most 32 inputs.", "inputs");
        else foreach (var input in inputs)
        {
            if (input is null) { Error("Each input must be an input definition.", "inputs"); continue; }
            if (string.IsNullOrEmpty(input.Name) || !Regex.IsMatch(input.Name, "^[A-Za-z][A-Za-z0-9_]{0,63}\\z") || !names.Add(input.Name))
                Error("Input names must be unique identifiers of at most 64 characters, starting with a letter.", "inputs");
            if (input.ValueType is not ("string" or "number" or "boolean")) Error($"Input '{input.Name}' has an unsupported value type.", "inputs");
            if (input.DefaultValue is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value && !ValidScalar(value, input.ValueType))
                Error($"Default for '{input.Name}' must match its type and limits.", "inputs");
        }
        if (!string.IsNullOrWhiteSpace(promptTemplate) && promptTemplate.Length <= 16000)
        {
            try { _ = Parse(promptTemplate, names); }
            catch (InvalidOperationException exception) { Error(exception.Message, "promptTemplate"); }
        }
        return new(errors);
    }

    public static async Task<CustomTaskDefinitionResolution> ResolveAsync(WorkflowDefinitionDocument document, CustomTaskService? tasks, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (document.Steps is null || document.Steps.Any(step => step is null))
            return new(document, new([new("definition.step.required", "Each step must be a node object.", "steps")]));
        var errors = new List<WorkflowDefinitionValidationError>();
        var cache = new Dictionary<string, CustomTaskSnapshot?>(StringComparer.Ordinal);
        var steps = new List<WorkflowDefinitionStep>();
        foreach (var step in document.Steps)
        {
            var settings = step.CustomTask;
            if (step.Uses != Uses)
            {
                if (settings is not null) errors.Add(Error(step.Id, "Only Custom task nodes may carry custom task settings."));
                steps.Add(step with { CustomTask = settings is null ? null : settings with { Snapshot = null } });
                continue;
            }
            CustomTaskSnapshot? snapshot = null;
            if (!string.IsNullOrWhiteSpace(settings?.TaskId))
            {
                if (!cache.TryGetValue(settings.TaskId, out snapshot))
                {
                    var task = tasks is null ? null : await tasks.GetAsync(settings.TaskId, token);
                    snapshot = task is null ? null : new(task.Id, task.Revision, task.Name, task.Description, task.PromptTemplate,
                        task.Inputs.Select(input => input with { DefaultValue = input.DefaultValue?.Clone() }).ToArray(), task.Runner with { });
                    cache[settings.TaskId] = snapshot;
                }
            }
            var enriched = settings is null ? null : settings with { Snapshot = snapshot, Inputs = Clone(settings.Inputs) };
            errors.AddRange(ValidateSettings(enriched).Select(message => Error(step.Id, message)));
            steps.Add(step with { CustomTask = enriched });
        }
        return new(document with { Steps = steps }, new(errors));
    }

    public static WorkflowDefinitionValidationResult ValidateRuntime(WorkflowDefinitionDocument document)
    {
        if (document.Steps is null || document.Steps.Any(step => step is null))
            return new([new("definition.step.required", "Each step must be a node object.", "steps")]);
        var errors = new List<WorkflowDefinitionValidationError>();
        foreach (var step in document.Steps)
        {
            if (step.Uses == Uses) errors.AddRange(ValidateSettings(step.CustomTask).Select(message => Error(step.Id, message)));
            else if (step.CustomTask is not null) errors.Add(Error(step.Id, "Only Custom task nodes may carry custom task settings."));
        }
        return new(errors);
    }

    public static PreparedCustomTaskExecution Prepare(WorkflowCustomTaskSettings settings, Workflow workflow)
    {
        Throw(ValidateSettings(settings));
        var snapshot = settings.Snapshot!;
        var inputs = ResolveInputs(snapshot.Inputs, settings.Inputs, out var errors);
        Throw(errors);
        var parts = Parse(snapshot.PromptTemplate, snapshot.Inputs.Select(input => input.Name).ToHashSet(StringComparer.Ordinal));
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var name in parts.Where(part => part.Source == "workflow").Select(part => part.Name!).Distinct(StringComparer.Ordinal))
            fields[name] = JsonSerializer.SerializeToElement(name switch
            {
                "issueUrl" => workflow.IssueUrl, "repositoryUrl" => workflow.RepositoryUrl, "baseBranch" => workflow.BaseBranch,
                "model" => workflow.Model, "planArtifact" => workflow.PlanArtifact, "pullRequestUrl" => workflow.PullRequestUrl,
                _ => throw new InvalidOperationException("Unknown workflow template field.")
            });
        var prompt = Render(parts, inputs, fields);
        if (Encoding.UTF8.GetByteCount(prompt) > MaximumPromptBytes) throw new InvalidOperationException("Rendered custom task prompt exceeds 131072 UTF-8 bytes.");
        return new(snapshot.Id, snapshot.Revision, snapshot.Name, inputs, fields, snapshot.Runner.TimeoutSeconds, prompt);
    }

    public static void ValidatePrepared(PreparedCustomTaskExecution prepared, WorkflowCustomTaskSettings settings)
    {
        Throw(ValidateSettings(settings));
        var snapshot = settings.Snapshot!;
        if (prepared is null || prepared.FormatVersion != 1 || prepared.TaskId != snapshot.Id || prepared.Revision != snapshot.Revision
            || prepared.Name != snapshot.Name || prepared.TimeoutSeconds != snapshot.Runner.TimeoutSeconds
            || prepared.Inputs is null || prepared.WorkflowFields is null || prepared.Prompt is null)
            throw new InvalidOperationException("Prepared custom task execution is malformed or does not match its pinned task.");
        var resolved = ResolveInputs(snapshot.Inputs, settings.Inputs, out var errors);
        Throw(errors);
        _ = ResolveInputs(snapshot.Inputs, prepared.Inputs, out var preparedErrors);
        Throw(preparedErrors);
        if (!SameValues(resolved, prepared.Inputs)) throw new InvalidOperationException("Prepared inputs do not match the pinned task inputs.");
        var parts = Parse(snapshot.PromptTemplate, snapshot.Inputs.Select(input => input.Name).ToHashSet(StringComparer.Ordinal));
        var references = parts.Where(part => part.Source == "workflow").Select(part => part.Name!).ToHashSet(StringComparer.Ordinal);
        if (!references.SetEquals(prepared.WorkflowFields.Keys)
            || prepared.WorkflowFields.Values.Any(value => value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
            throw new InvalidOperationException("Prepared workflow fields do not match the template references.");
        if (Encoding.UTF8.GetByteCount(prepared.Prompt) > MaximumPromptBytes || prepared.Prompt != Render(parts, prepared.Inputs, prepared.WorkflowFields))
            throw new InvalidOperationException("Prepared custom task prompt is invalid.");
    }

    private static List<string> ValidateSettings(WorkflowCustomTaskSettings? settings)
    {
        var errors = new List<string>();
        if (settings is null || string.IsNullOrWhiteSpace(settings.TaskId)) return ["Select a reusable custom task."];
        if (settings.Snapshot is not { } snapshot) return [$"Custom task '{settings.TaskId}' is unavailable or has no pinned snapshot."];
        if (snapshot.Id != settings.TaskId || snapshot.Revision < 1 || snapshot.Description is null)
            errors.Add("Custom task snapshot is malformed or does not match its selected task.");
        errors.AddRange(ValidateCatalog(snapshot.Name, snapshot.Description, snapshot.PromptTemplate, snapshot.Inputs, snapshot.Runner).Errors.Select(error => error.Message));
        if (errors.Count == 0) { _ = ResolveInputs(snapshot.Inputs, settings.Inputs, out var inputErrors); errors.AddRange(inputErrors); }
        return errors;
    }

    private static Dictionary<string, JsonElement> ResolveInputs(IReadOnlyList<CustomTaskInputDefinition> schema, IReadOnlyDictionary<string, JsonElement>? supplied, out List<string> errors)
    {
        errors = [];
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var names = schema.Select(input => input.Name).ToHashSet(StringComparer.Ordinal);
        if (supplied is not null) foreach (var key in supplied.Keys) if (!names.Contains(key)) errors.Add($"Unknown task input '{key}'.");
        foreach (var input in schema)
        {
            JsonElement value = default;
            var present = supplied?.TryGetValue(input.Name, out value) == true;
            if (!present && input.DefaultValue is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } fallback) { value = fallback; present = true; }
            if (!present) { if (input.Required) errors.Add($"Required input '{input.Name}' is missing."); continue; }
            if (!ValidScalar(value, input.ValueType)) errors.Add($"Input '{input.Name}' must be a bounded {input.ValueType} value.");
            else result[input.Name] = value.Clone();
        }
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result, Json)) > MaximumInputBytes) errors.Add("Resolved task inputs exceed 65536 UTF-8 bytes.");
        return result;
    }

    private static bool ValidScalar(JsonElement value, string? type) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 16000,
        "number" => IsWireSafeNumber(value),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        _ => false
    };
    private static bool IsWireSafeNumber(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number)
            || number is < -9007199254740991m or > 9007199254740991m
            || !value.TryGetDouble(out var floating) || !double.IsFinite(floating)) return false;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (!decimal.TryParse(floating.ToString("R", culture), System.Globalization.NumberStyles.Float, culture, out var roundTrip)
            || number != roundTrip) return false;
        // Decimal parsing may round an over-precise fractional token or underflow it to zero.
        // Check the original mathematical value too, so it cannot be accepted after losing digits.
        return NumericIdentity(value.GetRawText()) == NumericIdentity(number.ToString(culture));
    }
    private static string? NumericIdentity(string text)
    {
        var exponentAt = text.IndexOfAny(['e', 'E']);
        long exponent = 0;
        if (exponentAt >= 0)
        {
            if (!long.TryParse(text.AsSpan(exponentAt + 1), System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out exponent)) return null;
            text = text[..exponentAt];
        }
        var negative = text.StartsWith('-');
        if (negative) text = text[1..];
        var decimalAt = text.IndexOf('.');
        var fractionDigits = decimalAt >= 0 ? text.Length - decimalAt - 1 : 0;
        var digits = text.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return "0";
        var significant = digits.TrimEnd('0');
        try { exponent = checked(exponent - fractionDigits + digits.Length - significant.Length); }
        catch (OverflowException) { return null; }
        return (negative ? "-" : "") + significant + ":" + exponent.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private static Dictionary<string, JsonElement>? Clone(IReadOnlyDictionary<string, JsonElement>? values)
        => values?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
    private static WorkflowDefinitionValidationError Error(string node, string message) => new("definition.customTask.invalid", message, "steps[].customTask", node);
    private static void Throw(IReadOnlyCollection<string> errors) { if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors)); }
    private static bool SameValues(IReadOnlyDictionary<string, JsonElement> left, IReadOnlyDictionary<string, JsonElement> right)
        => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && pair.Value.ValueKind == value.ValueKind && Scalar(pair.Value) == Scalar(value));
    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!, JsonValueKind.Null => "",
        JsonValueKind.Number => value.GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonValueKind.True => "true", JsonValueKind.False => "false", _ => throw new InvalidOperationException("Invalid template scalar.")
    };
    private static string Render(IReadOnlyList<Part> parts, IReadOnlyDictionary<string, JsonElement> inputs, IReadOnlyDictionary<string, JsonElement> fields)
    {
        var result = new StringBuilder();
        var bytes = 0;
        foreach (var part in parts)
        {
            var text = part.Source is null ? part.Text : (part.Source == "input" ? inputs : fields).TryGetValue(part.Name!, out var value) ? Scalar(value) : "";
            var partBytes = Encoding.UTF8.GetByteCount(text);
            if (partBytes > MaximumPromptBytes - bytes)
                throw new InvalidOperationException("Rendered custom task prompt exceeds 131072 UTF-8 bytes.");
            bytes += partBytes;
            result.Append(text);
        }
        return result.ToString();
    }
    private static IReadOnlyList<Part> Parse(string template, HashSet<string> inputs)
    {
        var parts = new List<Part>(); var offset = 0;
        while (offset < template.Length)
        {
            var open = template.IndexOf("{{", offset, StringComparison.Ordinal);
            var close = template.IndexOf("}}", offset, StringComparison.Ordinal);
            if (close >= 0 && (open < 0 || close < open)) throw new InvalidOperationException("Prompt template has an unmatched closing delimiter.");
            if (open < 0) { parts.Add(new(template[offset..])); break; }
            if (open > offset) parts.Add(new(template[offset..open]));
            if (close < 0) throw new InvalidOperationException("Prompt template has an unmatched opening delimiter.");
            var token = template[(open + 2)..close];
            var separator = token.IndexOf('.');
            if (separator < 0) throw new InvalidOperationException($"Unknown template token '{{{{{token}}}}}'.");
            var source = token[..separator]; var name = token[(separator + 1)..];
            if ((source != "input" || !inputs.Contains(name)) && (source != "workflow" || !WorkflowNames.Contains(name)))
                throw new InvalidOperationException($"Unknown template token '{{{{{token}}}}}'.");
            parts.Add(new("", source, name)); offset = close + 2;
        }
        return parts;
    }
}
