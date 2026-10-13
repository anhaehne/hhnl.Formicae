using System.Text.Json;
using System.Text.Json.Serialization;

namespace hhnl.Formicae.Application.Workflows;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowCreateBranchSettings(Guid RepositoryId = default, string SourceBranch = "", string BranchName = "",
    IReadOnlyDictionary<string, JsonElement>? Inputs = null, IReadOnlyDictionary<string, CustomTaskInputBinding>? Bindings = null);
public sealed record PreparedCreateBranchExecution(string RepositoryUrl, string SourceBranch, string BranchName, string SourceSha, IReadOnlyDictionary<string, CustomTaskInputProvenance>? Provenance = null);

public static class CreateBranchDefinitions
{
    public const string Uses = "github.create-branch";

    public static IReadOnlyList<CustomTaskInputDefinition> Inputs { get; } = [new("repository", "string", true), new("sourceBranch", "string", true), new("targetBranch", "string", true)];

    // Git check-ref-format --branch rules; slash-separated names remain supported.
    public static bool ValidBranchName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 255
        && name != "@" && !name.StartsWith('-') && !name.EndsWith('.') && !name.Contains("..") && !name.Contains("@{")
        && !name.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || "~^:?*[\\".Contains(character))
        && name.Split('/').All(part => part.Length > 0 && !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.Ordinal));

    public static bool ValidValue(string name, JsonElement value) => value.ValueKind == JsonValueKind.String
        && (name == "repository" ? Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) && uri.Scheme == "https" : ValidBranchName(value.GetString()));

    public static IReadOnlyList<WorkflowDefinitionValidationError> ValidateStep(WorkflowDefinitionStep step)
    {
        var errors = new List<WorkflowDefinitionValidationError>();
        void Error(string message) => errors.Add(new("definition.createBranch.invalid", message, "steps[].createBranch", step.Id));
        if (step.Uses != Uses)
        {
            if (step.CreateBranch is not null) Error("Only Create branch tasks may carry branch settings.");
            return errors;
        }
        if (step.AiSettingsId is not null || step.Model is not null || step.PersonaId is not null || step.PersonaSnapshot is not null
            || step.EnvironmentId is not null || step.EnvironmentSnapshot is not null || step.ImageSelection is not null || step.ImageSnapshot is not null)
            Error("Create branch tasks cannot select agent or worker settings.");
        if (step.CreateBranch is not { } settings) { Error("Create branch settings are required."); return errors; }
        foreach (var name in (settings.Inputs?.Keys ?? []).Concat(settings.Bindings?.Keys ?? []))
            if (!Inputs.Any(input => input.Name == name)) Error($"Unknown branch input '{name}'.");
        bool Dynamic(string name) => settings.Inputs?.ContainsKey(name) == true || settings.Bindings?.ContainsKey(name) == true;
        foreach (var input in Inputs)
        {
            if (settings.Bindings?.TryGetValue(input.Name, out var binding) == true)
            {
                if (settings.Inputs?.ContainsKey(input.Name) == true) Error($"Input '{input.Name}' cannot have both a literal and a binding.");
                if (binding is null || string.IsNullOrWhiteSpace(binding.StepId) || string.IsNullOrWhiteSpace(binding.OutputName)) Error($"Binding for '{input.Name}' requires a producer and output name.");
            }
            if (settings.Inputs?.TryGetValue(input.Name, out var value) == true && !ValidValue(input.Name, value)) Error($"Invalid branch input '{input.Name}'.");
        }
        if (!Dynamic("repository") && settings.RepositoryId == Guid.Empty) Error("Select a connected GitHub repository.");
        if (!Dynamic("sourceBranch") && !ValidBranchName(settings.SourceBranch)) Error("Select a valid source branch.");
        if (!Dynamic("targetBranch") && !ValidBranchName(settings.BranchName)) Error("Enter a valid new branch name.");
        if (!Dynamic("sourceBranch") && !Dynamic("targetBranch") && settings.BranchName == settings.SourceBranch) Error("The new branch must differ from the source branch.");
        return errors;
    }
}
