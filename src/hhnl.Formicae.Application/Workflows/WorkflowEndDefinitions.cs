namespace hhnl.Formicae.Application.Workflows;

/// <summary>A terminal control node completes the entire execution on first arrival.</summary>
public static class WorkflowEndDefinitions
{
    public const string Uses = "builtins.end";

    public static IReadOnlyList<WorkflowDefinitionValidationError> ValidateStep(WorkflowDefinitionStep step)
    {
        if (step.Uses != Uses) return [];
        var errors = new List<WorkflowDefinitionValidationError>();
        if (step.NextStepId is not null || step.NextStepIds?.Count > 0 || step.NextStepPort is not null)
            errors.Add(new("definition.end.invalid", "End nodes cannot have outgoing connections.", "steps", step.Id));
        if (step.AiSettingsId is not null || step.Model is not null || step.PersonaId is not null || step.PersonaSnapshot is not null
            || step.EnvironmentId is not null || step.EnvironmentSnapshot is not null || step.ImageSelection is not null || step.ImageSnapshot is not null
            || step.Capabilities is not null || step.SecretReferences is not null || step.CustomTask is not null || step.Script is not null
            || step.Wait is not null || step.IssueComment is not null || step.Trigger is not null || step.Event is not null
            || step.Loop is not null || step.Parallel is not null || step.Decision is not null)
            errors.Add(new("definition.end.invalid", "End nodes cannot carry worker, event or other control settings.", "steps", step.Id));
        return errors;
    }
}
