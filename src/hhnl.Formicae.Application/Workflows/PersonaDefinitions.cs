namespace hhnl.Formicae.Application.Workflows;

public sealed record PersonaDefinitionResolution(WorkflowDefinitionDocument Document, WorkflowDefinitionValidationResult Validation);

/// <summary>Captures catalog revisions at save time and validates pinned execution without catalog reads.</summary>
public static class PersonaDefinitions
{
    public static bool IsAiTask(string? uses) => uses is "builtins.plan" or "builtins.implement" or "builtins.address-comments" or CustomTaskDefinitions.Uses;

    public static async Task<PersonaDefinitionResolution> ResolveAsync(
        WorkflowDefinitionDocument document, PersonaService? personas, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errors = new List<WorkflowDefinitionValidationError>();
        if (document.Steps is null)
            return new(document, new([new("definition.steps.required", "At least one step is required.", "steps")]));
        if (document.Steps.Any(step => step is null))
            return new(document, new([new("definition.step.required", "Workflow steps cannot contain null entries.", "steps")]));
        var resolved = new Dictionary<string, PersonaSnapshot?>(StringComparer.Ordinal)
        { [PersonaService.DefaultPersonaId] = PersonaService.DefaultSnapshot };
        async Task<PersonaSnapshot?> Resolve(string id)
        {
            if (resolved.TryGetValue(id, out var snapshot)) return snapshot;
            var persona = personas is null ? null : await personas.GetAsync(id, cancellationToken);
            snapshot = persona is null ? null : new(persona.Id, persona.Revision, persona.Name,
                persona.Instructions, persona.Tone, persona.OperatingConstraints);
            resolved[id] = snapshot;
            return snapshot;
        }
        var defaultId = document.DefaultPersonaId ?? PersonaService.DefaultPersonaId;
        if (await Resolve(defaultId) is null)
            errors.Add(new("definition.persona.missing", $"Workflow persona '{defaultId}' is unavailable.", "defaultPersonaId"));
        var steps = new List<WorkflowDefinitionStep>(document.Steps.Count);
        foreach (var step in document.Steps)
        {
            // A submitted snapshot never acts as a substitute for authoritative catalog resolution.
            var enriched = step with { PersonaSnapshot = null };
            if (!IsAiTask(step.Uses))
            {
                if (step.PersonaId is not null)
                    errors.Add(new("definition.persona.unsupported", "Only AI tasks can select a persona.", "steps[].personaId", step.Id));
            }
            else
            {
                var id = step.PersonaId ?? defaultId;
                var snapshot = await Resolve(id);
                if (snapshot is null)
                    errors.Add(new("definition.persona.missing", $"Persona '{id}' is unavailable.", "steps[].personaId", step.Id));
                else enriched = enriched with { PersonaSnapshot = snapshot };
            }
            steps.Add(enriched);
        }
        return new(document with { Steps = steps }, new(errors));
    }

    public static WorkflowDefinitionValidationResult ValidateRuntime(WorkflowDefinitionDocument document)
    {
        var errors = new List<WorkflowDefinitionValidationError>();
        if (document.Steps is null) return new([new("definition.steps.required", "At least one step is required.", "steps")]);
        if (document.Steps.Any(step => step is null)) return new([new("definition.step.required", "Workflow steps cannot contain null entries.", "steps")]);
        if (document.DefaultPersonaId is not null && string.IsNullOrWhiteSpace(document.DefaultPersonaId))
            errors.Add(new("definition.persona.invalid", "Workflow persona ID cannot be empty.", "defaultPersonaId"));
        foreach (var step in document.Steps)
        {
            void Error(string message) => errors.Add(new("definition.persona.snapshot.invalid", message, "steps[].personaSnapshot", step.Id));
            if (!IsAiTask(step.Uses))
            {
                if (step.PersonaId is not null || step.PersonaSnapshot is not null)
                    Error("Only AI tasks may have a persona selection or snapshot.");
                continue;
            }
            var id = step.PersonaId ?? document.DefaultPersonaId ?? PersonaService.DefaultPersonaId;
            if (string.IsNullOrWhiteSpace(id))
            {
                Error("Persona ID cannot be empty.");
                continue;
            }
            var snapshot = step.PersonaSnapshot;
            if (snapshot is null)
            {
                if (id != PersonaService.DefaultPersonaId) Error($"Persona '{id}' has no pinned snapshot.");
                continue;
            }
            if (snapshot.Id != id || snapshot.Revision < 1 || string.IsNullOrWhiteSpace(snapshot.Name) || snapshot.Name.Length > 120
                || snapshot.Instructions is null || snapshot.Instructions.Length > 16000
                || snapshot.Tone is null || snapshot.Tone.Length > 1000
                || snapshot.OperatingConstraints is null || snapshot.OperatingConstraints.Length > 8000)
                Error("Pinned persona snapshot is malformed or does not match the selected persona.");
            else if (id == PersonaService.DefaultPersonaId && snapshot != PersonaService.DefaultSnapshot)
                Error("The built-in default persona snapshot must preserve default behavior.");
        }
        return new(errors);
    }
}
