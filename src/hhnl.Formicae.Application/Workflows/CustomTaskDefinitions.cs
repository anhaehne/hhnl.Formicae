using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace hhnl.Formicae.Application.Workflows;

public static class CustomTaskDefinitions
{
    public const string Uses = "builtins.custom-task";
    public const string AgentUses = "builtins.agent-task";
    public static bool IsAgentTask(string? uses) => uses is Uses or AgentUses;

    private static CustomTaskSnapshot InlineSnapshot(WorkflowDefinitionStep step, AgentTaskDefinition definition) =>
        new($"agent:{step.Id}", 1, step.DisplayName ?? "Agent task", "", definition.PromptTemplate,
            definition.Inputs, definition.Runner, definition.Outputs);
    public const int MaximumPromptBytes = 131072;
    private const int MaximumInputBytes = 65536;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> WorkflowNames = ["issueUrl", "repositoryUrl", "baseBranch", "model", "planArtifact", "pullRequestUrl"];
    private sealed record Part(string Text, string? Source = null, string? Name = null);

    public static WorkflowDefinitionValidationResult ValidateCatalog(string? name, string? description, string? promptTemplate,
        IReadOnlyList<CustomTaskInputDefinition>? inputs, CustomTaskRunnerSettings? runner, IReadOnlyList<CustomTaskOutputDefinition>? outputs = null)
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
        if (outputs?.Count > 32) Error("Output schema may contain at most 32 outputs.", "outputs");
        var outputNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var output in outputs ?? [])
        {
            if (output is null || string.IsNullOrEmpty(output.Name) || !Regex.IsMatch(output.Name, "^[A-Za-z][A-Za-z0-9_]{0,63}\\z") || !outputNames.Add(output.Name))
                Error("Output names must be unique identifiers of at most 64 characters, starting with a letter.", "outputs");
            if (output is not null && output.ValueType is not ("string" or "number" or "boolean"))
                Error($"Output '{output.Name}' has an unsupported value type.", "outputs");
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
            if (!IsAgentTask(step.Uses))
            {
                if (settings is not null) errors.Add(Error(step.Id, "Only agent task nodes may carry custom task settings."));
                steps.Add(step with { CustomTask = settings is null ? null : settings with { Snapshot = null } });
                continue;
            }
            if (step.Uses == AgentUses)
            {
                if (settings?.Definition is not { } definition)
                {
                    errors.Add(Error(step.Id, "Agent task settings are required."));
                    steps.Add(step with { CustomTask = settings is null ? null : settings with { Snapshot = null } });
                    continue;
                }
                var inline = InlineSnapshot(step, definition);
                var configured = settings with { TaskId = inline.Id, Snapshot = inline, Inputs = Clone(settings.Inputs) };
                errors.AddRange(ValidateSettings(configured).Select(message => Error(step.Id, message)));
                steps.Add(step with { CustomTask = configured });
                continue;
            }
            if (settings?.Definition is not null) errors.Add(Error(step.Id, "Inline definitions require an Agent task node."));
            CustomTaskSnapshot? snapshot = null;
            if (!string.IsNullOrWhiteSpace(settings?.TaskId))
            {
                if (!cache.TryGetValue(settings.TaskId, out snapshot))
                {
                    var task = tasks is null ? null : await tasks.GetAsync(settings.TaskId, token);
                    snapshot = task is null ? null : new(task.Id, task.Revision, task.Name, task.Description, task.PromptTemplate,
                        task.Inputs.Select(input => input with { DefaultValue = input.DefaultValue?.Clone() }).ToArray(), task.Runner with { }, task.Outputs.ToArray());
                    cache[settings.TaskId] = snapshot;
                }
            }
            var enriched = settings is null ? null : settings with { Snapshot = snapshot, Inputs = Clone(settings.Inputs) };
            errors.AddRange(ValidateSettings(enriched).Select(message => Error(step.Id, message)));
            steps.Add(step with { CustomTask = enriched });
        }
        var resolved = document with { Steps = steps };
        errors.AddRange(ValidateBindings(resolved));
        return new(resolved, new(errors));
    }

    public static WorkflowDefinitionValidationResult ValidateRuntime(WorkflowDefinitionDocument document)
    {
        if (document.Steps is null || document.Steps.Any(step => step is null))
            return new([new("definition.step.required", "Each step must be a node object.", "steps")]);
        var errors = new List<WorkflowDefinitionValidationError>();
        foreach (var step in document.Steps)
        {
            if (IsAgentTask(step.Uses))
            {
                errors.AddRange(ValidateSettings(step.CustomTask).Select(message => Error(step.Id, message)));
                if (step.Uses == AgentUses)
                {
                    if (step.CustomTask?.Definition is not { } definition || step.CustomTask.Snapshot is not { } snapshot
                        || JsonSerializer.Serialize(InlineSnapshot(step, definition), Json) != JsonSerializer.Serialize(snapshot, Json))
                        errors.Add(Error(step.Id, "Agent task snapshot does not match its inline definition."));
                }
                else if (step.CustomTask?.Definition is not null) errors.Add(Error(step.Id, "Inline definitions require an Agent task node."));
            }
            else if (step.CustomTask is not null) errors.Add(Error(step.Id, "Only agent task nodes may carry custom task settings."));
        }
        errors.AddRange(ValidateBindings(document));
        return new(errors);
    }

    public static PreparedCustomTaskExecution Prepare(WorkflowCustomTaskSettings settings, Workflow workflow, IReadOnlyDictionary<string, CustomTaskInputProvenance>? provenance = null)
    {
        Throw(ValidateSettings(settings));
        var snapshot = settings.Snapshot!;
        var inputs = ResolveInputs(snapshot.Inputs, BoundValues(settings, provenance), out var errors);
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
        var prompt = WithOutputInstruction(Render(parts, inputs, fields), snapshot);
        if (Encoding.UTF8.GetByteCount(prompt) > MaximumPromptBytes) throw new InvalidOperationException("Rendered custom task prompt exceeds 131072 UTF-8 bytes.");
        return new(snapshot.Id, snapshot.Revision, snapshot.Name, inputs, fields, snapshot.Runner.TimeoutSeconds, prompt, Provenance: provenance?.ToDictionary(pair => pair.Key, pair => pair.Value with { Value = pair.Value.Value?.Clone() }, StringComparer.Ordinal));
    }

    public static void ValidatePrepared(PreparedCustomTaskExecution prepared, WorkflowCustomTaskSettings settings)
    {
        Throw(ValidateSettings(settings));
        var snapshot = settings.Snapshot!;
        if (prepared is null || prepared.FormatVersion != 1 || prepared.TaskId != snapshot.Id || prepared.Revision != snapshot.Revision
            || prepared.Name != snapshot.Name || prepared.TimeoutSeconds != snapshot.Runner.TimeoutSeconds
            || prepared.Inputs is null || prepared.WorkflowFields is null || prepared.Prompt is null)
            throw new InvalidOperationException("Prepared custom task execution is malformed or does not match its pinned task.");
        var resolved = ResolveInputs(snapshot.Inputs, BoundValues(settings, prepared.Provenance), out var errors);
        Throw(errors);
        _ = ResolveInputs(snapshot.Inputs, prepared.Inputs, out var preparedErrors);
        Throw(preparedErrors);
        if (!SameValues(resolved, prepared.Inputs)) throw new InvalidOperationException("Prepared inputs do not match the pinned task inputs.");
        var parts = Parse(snapshot.PromptTemplate, snapshot.Inputs.Select(input => input.Name).ToHashSet(StringComparer.Ordinal));
        var references = parts.Where(part => part.Source == "workflow").Select(part => part.Name!).ToHashSet(StringComparer.Ordinal);
        if (!references.SetEquals(prepared.WorkflowFields.Keys)
            || prepared.WorkflowFields.Values.Any(value => value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
            throw new InvalidOperationException("Prepared workflow fields do not match the template references.");
        var rendered = Render(parts, prepared.Inputs, prepared.WorkflowFields);
        if (Encoding.UTF8.GetByteCount(prepared.Prompt) > MaximumPromptBytes
            || prepared.Prompt != WithOutputInstruction(rendered, snapshot) && prepared.Prompt != WithLegacyOutputInstruction(rendered, snapshot))
            throw new InvalidOperationException("Prepared custom task prompt is invalid.");
    }

    private static List<string> ValidateSettings(WorkflowCustomTaskSettings? settings)
    {
        var errors = new List<string>();
        if (settings is null || string.IsNullOrWhiteSpace(settings.TaskId)) return ["Select a reusable custom task."];
        if (settings.Snapshot is not { } snapshot) return [$"Custom task '{settings.TaskId}' is unavailable or has no pinned snapshot."];
        if (snapshot.Id != settings.TaskId || snapshot.Revision < 1 || snapshot.Description is null)
            errors.Add("Custom task snapshot is malformed or does not match its selected task.");
        errors.AddRange(ValidateCatalog(snapshot.Name, snapshot.Description, snapshot.PromptTemplate, snapshot.Inputs, snapshot.Runner, snapshot.Outputs).Errors.Select(error => error.Message));
        var bindings = settings.Bindings ?? new Dictionary<string, CustomTaskInputBinding>();
        foreach (var (name, binding) in bindings)
        {
            if (snapshot.Inputs?.Any(input => input?.Name == name) != true) errors.Add($"Unknown bound input '{name}'.");
            if (settings.Inputs?.ContainsKey(name) == true) errors.Add($"Input '{name}' cannot have both a literal and a binding.");
            if (binding is null || string.IsNullOrWhiteSpace(binding.StepId) || string.IsNullOrWhiteSpace(binding.OutputName)) errors.Add($"Binding for '{name}' requires a producer step and output name.");
        }
        if (errors.Count == 0)
        {
            _ = ResolveInputs((snapshot.Inputs ?? []).Where(input => !bindings.ContainsKey(input.Name)).ToArray(), settings.Inputs, out var inputErrors);
            errors.AddRange(inputErrors);
        }
        return errors;
    }

    public static IReadOnlyList<CustomTaskOutputDefinition> OutputSchemaFor(WorkflowDefinitionStep step) =>
        step.Uses == "github.issue-created" || step.Trigger?.Type == WorkflowTriggerType.DevOpsIssueCreated
            ? [new("issue", "string", true), new("issueId", "number", true)]
            : WorkflowWaitRegistry.Default.TryGet(step.Uses, out var wait) ? wait.Outputs : step.Uses == WorkflowExecutionExtensions.ScriptUses ? [new("output", "string", true)] : step.CustomTask?.Snapshot?.Outputs ?? [];

    public static IReadOnlyList<CustomTaskInputDefinition> InputSchemaFor(WorkflowDefinitionStep step) =>
        step.Wait is not null ? [new("issueNumber", "number", true)] : step.Uses == IssueCommentDefinitions.Uses ? IssueCommentDefinitions.Inputs : step.CustomTask?.Snapshot?.Inputs ?? [];
    public static IReadOnlyDictionary<string, CustomTaskInputBinding> BindingsFor(WorkflowDefinitionStep step) =>
        step.Wait?.IssueNumberBinding is { } waitBinding ? new Dictionary<string, CustomTaskInputBinding> { ["issueNumber"] = waitBinding } :
        (step.Uses == IssueCommentDefinitions.Uses ? step.IssueComment?.Bindings : step.CustomTask?.Bindings) ?? new Dictionary<string, CustomTaskInputBinding>();

    public static IReadOnlyDictionary<string, JsonElement> ParseProducerOutputs(WorkflowDefinitionStep step, string json)
    {
        if (step.Uses != "github.issue-created" && step.Trigger?.Type != WorkflowTriggerType.DevOpsIssueCreated)
            return ParseOutputs(json, OutputSchemaFor(step));
        // Preserve full event evidence. Consumer input limits are applied when preparing the consumer.
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Json)
            ?? throw new InvalidOperationException("Event outputs are missing.");
        if (values.Count != 2 || !values.TryGetValue("issue", out var issue) || issue.ValueKind != JsonValueKind.String
            || !values.TryGetValue("issueId", out var id) || !IssueCommentDefinitions.ValidValue("issueId", id))
            throw new InvalidOperationException("Event outputs are invalid.");
        return values;
    }

    public static IReadOnlyDictionary<string, JsonElement> ParseOutputs(string response, IReadOnlyList<CustomTaskOutputDefinition> schema)
    {
        if (Encoding.UTF8.GetByteCount(response) > MaximumInputBytes) throw new InvalidOperationException("Custom task outputs exceed 65536 UTF-8 bytes.");
        try
        {
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Custom task final response must be a JSON object.");
            var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var definition = schema.SingleOrDefault(output => output.Name == property.Name);
                if (definition is null) throw new InvalidOperationException($"Unknown custom task output '{property.Name}'.");
                if (!result.TryAdd(property.Name, property.Value.Clone())) throw new InvalidOperationException($"Duplicate custom task output '{property.Name}'.");
                if (!ValidScalar(property.Value, definition.ValueType)) throw new InvalidOperationException($"Output '{property.Name}' must be a bounded {definition.ValueType} value.");
            }
            foreach (var output in schema.Where(output => output.Required))
                if (!result.ContainsKey(output.Name)) throw new InvalidOperationException($"Required output '{output.Name}' is missing.");
            if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result, Json)) > MaximumInputBytes)
                throw new InvalidOperationException("Serialized custom task outputs exceed 65536 UTF-8 bytes.");
            return result;
        }
        catch (JsonException exception) { throw new InvalidOperationException("Custom task final response must be strict JSON matching its output schema; omit markdown and commentary.", exception); }
    }

    private static string WithOutputInstruction(string prompt, CustomTaskSnapshot snapshot)
        => EnsureOutputInstruction(prompt, snapshot.Outputs);

    public static string EnsureOutputInstruction(string prompt, IReadOnlyList<CustomTaskOutputDefinition> outputs)
    {
        if (outputs.Count == 0) return prompt;
        var instruction = OutputInstruction(outputs);
        return prompt.Contains(instruction, StringComparison.Ordinal) ? prompt : prompt + "\n\n" + instruction;
    }

    public static string OutputInstruction(IReadOnlyList<CustomTaskOutputDefinition> outputs)
        => "Return your final response as one strict JSON object containing only the declared named outputs. "
            + "Do not use markdown fences or additional commentary. Include every required output; omit optional outputs when unavailable. "
            + "Do not return null values, duplicate names or undeclared names. Strings must be at most 16000 characters; "
            + "numbers must be within ±9007199254740991 with at most 28 decimal places; booleans must be true or false. "
            + "The JSON object must be at most 65536 UTF-8 bytes. Output schema: " + JsonSerializer.Serialize(outputs, Json);

    private static string WithLegacyOutputInstruction(string prompt, CustomTaskSnapshot snapshot)
        => snapshot.Outputs.Count == 0 ? prompt : prompt + "\n\nReturn your final response as one strict JSON object containing only the declared named outputs. Do not use markdown fences or additional commentary. Omit optional outputs when unavailable. Output schema: " + JsonSerializer.Serialize(snapshot.Outputs, Json);

    private static IReadOnlyDictionary<string, JsonElement> BoundValues(WorkflowCustomTaskSettings settings, IReadOnlyDictionary<string, CustomTaskInputProvenance>? provenance)
    {
        var bindings = settings.Bindings ?? new Dictionary<string, CustomTaskInputBinding>();
        if ((provenance?.Count ?? 0) != bindings.Count) throw new InvalidOperationException("Prepared input provenance does not match the configured bindings.");
        var values = Clone(settings.Inputs) ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, binding) in bindings)
        {
            if (provenance is null || !provenance.TryGetValue(name, out var source) || source is null
                || source.StepId != binding.StepId || source.OutputName != binding.OutputName)
                throw new InvalidOperationException($"Binding '{name}' has no valid frozen producer identity.");
            WorkflowVariableDefinitions.ValidateEvidence(source);
            if (source.Value is { } value) values[name] = value.Clone();
        }
        return values;
    }


    public static IReadOnlyList<WorkflowDefinitionValidationError> ValidateBindings(WorkflowDefinitionDocument document)
    {
        var errors = new List<WorkflowDefinitionValidationError>();
        errors.AddRange(WorkflowVariableDefinitions.Validate(document));
        if (errors.Count > 0 || !document.Steps.Any(step => BindingsFor(step).Count > 0)) return errors;
        WorkflowDefinitionDocument plan;
        try { plan = WorkflowNodeDefinitions.Normalize(document); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or KeyNotFoundException or NullReferenceException)
        { return [new("definition.customTask.invalid", "Bindings require a valid control graph.", "steps[].customTask.bindings")]; }
        if (plan.Steps.Select(step => step.Id).Distinct().Count() != plan.Steps.Count) return [new("definition.customTask.invalid", "Bindings require unique step IDs.")];
        var nodes = plan.Steps.ToDictionary(step => step.Id, StringComparer.Ordinal);
        string? LoopFor(string id) => plan.Loops?.FirstOrDefault(loop => loop.BodyStepIds.Contains(id))?.Id;
        IEnumerable<string> Next(string id)
        {
            if (!nodes.TryGetValue(id, out var node)) return [];
            var loop = plan.Loops?.FirstOrDefault(loop => loop.BodyStepIds.LastOrDefault() == id);
            if (loop is not null) return [loop.ExitStepId];
            if (node.Decision is { } decision) return [decision.TrueStepId, decision.FalseStepId];
            if (node.Parallel is { } parallel) return parallel.BranchStepIds.Concat(node.NextStepId is null ? [] : new[] { node.NextStepId });
            return WorkflowGraphDefinitions.Successors(node);
        }
        bool Reach(string start, string target, string? blocked = null)
        {
            var visited = new HashSet<string>(); var pending = new Stack<string>(); pending.Push(start);
            while (pending.TryPop(out var id))
            {
                if (id == blocked || !visited.Add(id)) continue;
                if (id == target) return true;
                foreach (var next in Next(id)) pending.Push(next);
            }
            return false;
        }
        var entries = new[] { plan.StartStepId }.Concat(plan.Triggers?.Select(trigger => trigger.NextStepId ?? plan.StartStepId) ?? []).Distinct().ToArray();
        var eventEntries = (plan.Triggers ?? []).Select(evt => (Id: evt.Id, Entry: evt.NextStepId ?? plan.StartStepId)).ToList();
        if (!WorkflowStartDefinitions.HasStartNodes(document) || !string.IsNullOrEmpty(document.StartStepId))
            eventEntries.Add(("", plan.StartStepId));
        foreach (var consumer in plan.Steps)
        foreach (var (name, configuredBinding) in BindingsFor(consumer))
        foreach (var binding in WorkflowVariableDefinitions.Expand(document, configuredBinding))
        {
            if (binding is null || string.IsNullOrWhiteSpace(binding.StepId) || string.IsNullOrWhiteSpace(binding.OutputName)) continue;
            var input = InputSchemaFor(consumer).FirstOrDefault(input => input?.Name == name);
            nodes.TryGetValue(binding.StepId, out var producer);
            producer ??= document.Steps.FirstOrDefault(step => step.Id == binding.StepId);
            var output = producer is null ? null : OutputSchemaFor(producer).FirstOrDefault(output => output?.Name == binding.OutputName);
            if (input is null || output is null || input.ValueType != output.ValueType)
                errors.Add(Error(consumer.Id, $"Binding '{name}' must reference a declared producer output with the same scalar type."));
            else if (producer!.Uses == "github.issue-created" || producer.Trigger?.Type == WorkflowTriggerType.DevOpsIssueCreated)
            {
                var eventEntry = eventEntries.FirstOrDefault(entry => entry.Id == producer.Id);
                if (eventEntry.Entry is null || !Reach(eventEntry.Entry, consumer.Id)
                    || eventEntries.Any(entry => entry.Id != producer.Id && Reach(entry.Entry, consumer.Id)))
                    errors.Add(Error(consumer.Id, $"Event '{producer.Id}' must be the guaranteed selected entrypoint for consumer '{consumer.Id}'."));
            }
            else if (producer.Id == consumer.Id || !Reach(producer.Id, consumer.Id)
                || (!WorkflowGraphDefinitions.IsGraph(plan) && entries.Any(entry => Reach(entry, consumer.Id, producer.Id)))
                || (WorkflowGraphDefinitions.IsGraph(plan) && WorkflowStartDefinitions.HasStartNodes(document)
                    && entries.Any(entry => Reach(entry, consumer.Id) && !Reach(entry, producer.Id))))
                errors.Add(Error(consumer.Id, $"Producer '{producer.Id}' must be guaranteed to execute before consumer '{consumer.Id}'; self, downstream and conditional sources are invalid."));
            else if (LoopFor(producer.Id) is { } producerLoop && producerLoop != LoopFor(consumer.Id))
                errors.Add(Error(consumer.Id, "Bindings cannot leave a loop body or cross loops."));
        }
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

    public static bool ValidScalar(JsonElement value, string? type) => type switch
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
