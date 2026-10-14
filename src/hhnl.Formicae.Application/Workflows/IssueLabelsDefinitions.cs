using System.Text.Json;
using System.Text.Json.Serialization;

namespace hhnl.Formicae.Application.Workflows;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowIssueLabelsSettings(IReadOnlyDictionary<string, JsonElement>? Inputs = null,
    IReadOnlyDictionary<string, CustomTaskInputBinding>? Bindings = null);
public sealed record PreparedIssueLabelsExecution(IReadOnlyDictionary<string, JsonElement> Inputs,
    IReadOnlyDictionary<string, CustomTaskInputProvenance> Provenance);

public static class IssueLabelsDefinitions
{
    public const string Uses = "github.add-issue-labels";
    public static IReadOnlyList<CustomTaskInputDefinition> Inputs { get; } = [new("issueId", "number", true), new("labels", "string", true)];

    public static IReadOnlyList<WorkflowDefinitionValidationError> ValidateStep(WorkflowDefinitionStep step)
    {
        var errors = new List<WorkflowDefinitionValidationError>();
        void Error(string message) => errors.Add(new("definition.issueLabels.invalid", message, "steps[].issueLabels", step.Id));
        if (step.Uses != Uses)
        {
            if (step.IssueLabels is not null) Error("Only Add issue labels tasks may carry issue labels settings.");
            return errors;
        }
        if (step.AiSettingsId is not null || step.Model is not null || step.PersonaId is not null || step.PersonaSnapshot is not null
            || step.EnvironmentId is not null || step.EnvironmentSnapshot is not null || step.ImageSelection is not null || step.ImageSnapshot is not null)
            Error("Add issue labels tasks cannot select agent or worker settings.");
        if (step.IssueLabels is not { } settings) { Error("Add issue labels settings are required."); return errors; }
        foreach (var name in (settings.Inputs?.Keys ?? []).Concat(settings.Bindings?.Keys ?? []))
            if (!Inputs.Any(input => input.Name == name)) Error($"Unknown labels input '{name}'.");
        foreach (var input in Inputs)
        {
            CustomTaskInputBinding? binding = null;
            JsonElement value = default;
            var bound = settings.Bindings?.TryGetValue(input.Name, out binding) == true;
            var supplied = settings.Inputs?.TryGetValue(input.Name, out value) == true;
            if (bound && supplied) Error($"Input '{input.Name}' cannot have both a literal and a binding.");
            if (bound && (binding is null || string.IsNullOrWhiteSpace(binding.StepId) || string.IsNullOrWhiteSpace(binding.OutputName)))
                Error($"Binding for '{input.Name}' requires a producer and output name.");
            if (!bound && !supplied) Error($"Required input '{input.Name}' is missing.");
            if (supplied && !ValidValue(input.Name, value)) Error($"Input '{input.Name}' must be {(input.Name == "issueId" ? "a positive integer issue number" : "one to 100 labels of at most 50 characters each")}.");
        }
        return errors;
    }

    public static string[] ParseLabels(string text) => text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static bool ValidLabels(string? text) => text is not null && text.Length <= 16000 && ParseLabels(text) is { Length: > 0 and <= 100 } labels && labels.All(label => label.Length <= 50 && !label.Any(char.IsControl));

    public static bool ValidValue(string name, JsonElement value) => name switch
    {
        "issueId" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0,
        "labels" => value.ValueKind == JsonValueKind.String && ValidLabels(value.GetString()),
        _ => false
    };
}
