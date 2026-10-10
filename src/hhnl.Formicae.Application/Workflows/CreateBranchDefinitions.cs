using System.Text.Json.Serialization;

namespace hhnl.Formicae.Application.Workflows;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowCreateBranchSettings(Guid RepositoryId, string SourceBranch, string BranchName);
public sealed record PreparedCreateBranchExecution(string RepositoryUrl, string SourceBranch, string BranchName, string SourceSha);

public static class CreateBranchDefinitions
{
    public const string Uses = "github.create-branch";

    // Git check-ref-format --branch rules; slash-separated names remain supported.
    public static bool ValidBranchName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 255
        && name != "@" && !name.StartsWith('-') && !name.EndsWith('.') && !name.Contains("..") && !name.Contains("@{")
        && !name.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || "~^:?*[\\".Contains(character))
        && name.Split('/').All(part => part.Length > 0 && !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.Ordinal));

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
        if (settings.RepositoryId == Guid.Empty) Error("Select a connected GitHub repository.");
        if (!ValidBranchName(settings.SourceBranch)) Error("Select a valid source branch.");
        if (!ValidBranchName(settings.BranchName)) Error("Enter a valid new branch name.");
        if (settings.BranchName == settings.SourceBranch) Error("The new branch must differ from the source branch.");
        return errors;
    }
}
