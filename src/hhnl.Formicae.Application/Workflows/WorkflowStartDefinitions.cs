namespace hhnl.Formicae.Application.Workflows;

/// <summary>First-class entrypoints compiled into the existing durable execution model.</summary>
public static class WorkflowStartDefinitions
{
    public const string Uses = "builtins.start";
    public static bool HasStartNodes(WorkflowDefinitionDocument document) => document.Steps.Any(node => node.Uses == Uses || node.Event is not null || WorkflowEventDefinitions.IsEvent(node.Uses));

    public static WorkflowDefinitionValidationResult Validate(WorkflowDefinitionDocument document)
    {
        var errors = new List<WorkflowDefinitionValidationError>();
        void Error(string message, string? id = null) => errors.Add(new("definition.start.invalid", message, "steps", id));
        foreach (var node in document.Steps.Where(node => node.Event is not null || WorkflowEventDefinitions.IsEvent(node.Uses) && node.Uses != Uses))
        {
            if (!WorkflowEventRegistry.Default.TryGet(node.Uses, out var definition)) { Error("Unknown event definition.", node.Id); continue; }
            if (node.Event is not { ValueKind: System.Text.Json.JsonValueKind.Object } settings || node.Trigger is not null)
            { Error("Event nodes require event configuration only.", node.Id); continue; }
            try { foreach (var message in definition.Validate(settings)) Error(message, node.Id); }
            catch (System.Text.Json.JsonException) { Error("Invalid event configuration.", node.Id); }
        }
        if (errors.Count > 0) return new(errors);
        document = WorkflowEventDefinitions.Adapt(document);
        if (document.Steps.Any(node => string.IsNullOrWhiteSpace(node.Id))
            || document.Steps.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() != document.Steps.Count)
            return new([new("definition.start.invalid", "Node IDs must be nonempty and unique.", "steps")]);
        var starts = document.Steps.Where(node => node.Uses == Uses).ToArray();
        var manual = starts.Where(node => node.Trigger?.Type == WorkflowTriggerType.Manual).ToArray();
        if (manual.Length > 1) Error("A workflow can have only one manual start node.");
        if (manual.Length == 1 && document.StartStepId != manual[0].Id)
            Error("Default manual entry must reference the manual start node.", manual[0].Id);
        if (manual.Length == 0 && !string.IsNullOrEmpty(document.StartStepId))
            Error("A trigger-only workflow cannot have a manual entry.");
        var nodes = document.Steps.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var start in starts)
        {
            if (start.Trigger is null || !Enum.IsDefined(start.Trigger.Type)) Error("Select a supported start type.", start.Id);
            if (start.Trigger is { RepositoryIds: null }) Error("Start repository selections must be a list.", start.Id);
            if (start.NextStepId is null || !nodes.TryGetValue(start.NextStepId, out var next)
                || next.Uses is Uses or WorkflowNodeDefinitions.TriggerUses || start.NextStepIds?.Count > 0
                || start.NextStepPort is not null)
                Error("Start nodes require one outgoing connection to an execution node.", start.Id);
            if (start.AiSettingsId is not null || start.Model is not null || start.PersonaId is not null
                || start.EnvironmentId is not null || start.Loop is not null || start.Parallel is not null || start.Decision is not null)
                Error("Start nodes cannot carry task or control settings.", start.Id);
            if (start.Trigger?.Type == WorkflowTriggerType.Webhook && start.Trigger.Enabled
                && !WorkflowExecutionExtensions.ValidName(start.Trigger.WebhookSecretName ?? ""))
                Error("Enabled webhook starts require a valid operator-managed secret name.", start.Id);
            foreach (var node in nodes.Values)
                if (WorkflowGraphDefinitions.Successors(node).Contains(start.Id)
                    || node.Loop?.BodyStepId == start.Id || node.Parallel?.BranchStepIds.Contains(start.Id) == true
                    || node.Decision?.TrueStepId == start.Id || node.Decision?.FalseStepId == start.Id)
                    Error("Start nodes cannot have incoming control connections.", start.Id);
        }
        if (errors.Count > 0) return new(errors);
        var compiled = Compile(document);
        return WorkflowGraphDefinitions.IsGraph(compiled)
            ? WorkflowGraphDefinitions.Validate(compiled, allowAlternativeEntries: true)
            : WorkflowNodeDefinitions.Validate(compiled);
    }

    // Called only for execution/validation. Saved versions retain the actual start nodes.
    public static WorkflowDefinitionDocument Compile(WorkflowDefinitionDocument document)
    {
        document = WorkflowEventDefinitions.Adapt(document);
        var manual = document.Steps.FirstOrDefault(node => node.Uses == Uses && node.Trigger?.Type == WorkflowTriggerType.Manual);
        var starts = document.Steps.Where(node => node.Uses == Uses).ToArray();
        return document with
        {
            StartStepId = manual?.NextStepId ?? starts.First().NextStepId!,
            Steps = document.Steps.Where(node => node != manual).Select(node => node.Uses == Uses
                ? node with { Uses = WorkflowNodeDefinitions.TriggerUses } : node).ToArray()
        };
    }

    public static WorkflowDefinitionStep? ManualStart(WorkflowDefinitionDocument document)
        => WorkflowEventDefinitions.Adapt(document).Steps.SingleOrDefault(node => node.Uses == Uses && node.Trigger?.Type == WorkflowTriggerType.Manual);
}
