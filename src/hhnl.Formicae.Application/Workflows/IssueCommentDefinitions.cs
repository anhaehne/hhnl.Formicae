using System.Text.Json;
using System.Text.Json.Serialization;

namespace hhnl.Formicae.Application.Workflows;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowIssueCommentSettings(IReadOnlyDictionary<string, JsonElement>? Inputs = null,
    IReadOnlyDictionary<string, CustomTaskInputBinding>? Bindings = null);
public sealed record PreparedIssueCommentExecution(IReadOnlyDictionary<string, JsonElement> Inputs,
    IReadOnlyDictionary<string, CustomTaskInputProvenance> Provenance);

public static class IssueCommentDefinitions
{
    public const string Uses = "github.add-issue-comment";
    public static IReadOnlyList<CustomTaskInputDefinition> Inputs { get; } = [new("issueId", "number", true), new("text", "string", true)];

    public static IReadOnlyList<WorkflowDefinitionValidationError> ValidateStep(WorkflowDefinitionStep step)
    {
        var errors = new List<WorkflowDefinitionValidationError>();
        void Error(string message) => errors.Add(new("definition.issueComment.invalid", message, "steps[].issueComment", step.Id));
        if (step.Uses != Uses)
        {
            if (step.IssueComment is not null) Error("Only Add issue comment tasks may carry issue comment settings.");
            return errors;
        }
        if (step.AiSettingsId is not null || step.Model is not null || step.PersonaId is not null || step.PersonaSnapshot is not null
            || step.EnvironmentId is not null || step.EnvironmentSnapshot is not null || step.ImageSelection is not null || step.ImageSnapshot is not null)
            Error("Add issue comment tasks cannot select agent or worker settings.");
        if (step.IssueComment is not { } settings) { Error("Add issue comment settings are required."); return errors; }
        foreach (var name in (settings.Inputs?.Keys ?? []).Concat(settings.Bindings?.Keys ?? []))
            if (!Inputs.Any(input => input.Name == name)) Error($"Unknown comment input '{name}'.");
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
            if (supplied && !ValidValue(input.Name, value)) Error($"Input '{input.Name}' must be {(input.Name == "issueId" ? "a positive integer issue number" : "nonblank text of at most 16000 characters")}.");
        }
        return errors;
    }

    public static bool ValidValue(string name, JsonElement value) => name switch
    {
        "issueId" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0,
        "text" => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) && value.GetString()!.Length <= 16000,
        _ => false
    };
}
