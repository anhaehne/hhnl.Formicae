namespace hhnl.Formicae.Application.Workflows;

public sealed class WorkflowDefinitionValidator
{
    private static readonly IReadOnlyDictionary<string, TaskRunKind> SupportedBuiltins = new Dictionary<string, TaskRunKind>(StringComparer.Ordinal)
    {
        ["builtins.plan"] = TaskRunKind.Plan,
        ["builtins.implement"] = TaskRunKind.Implement,
        ["builtins.create-pull-request"] = TaskRunKind.CreatePullRequest,
        ["builtins.address-comments"] = TaskRunKind.AddressComments
    };

    public WorkflowDefinitionValidationResult ValidateDefinitionName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new WorkflowDefinitionValidationResult([
                new WorkflowDefinitionValidationError("definition.name.required", "Definition name is required.", "name")
            ]);
        }

        return WorkflowDefinitionValidationResult.Valid;
    }

    public WorkflowDefinitionValidationResult Validate(WorkflowDefinitionDocument? document)
    {
        var errors = new List<WorkflowDefinitionValidationError>();
        if (document is null)
        {
            errors.Add(new WorkflowDefinitionValidationError("definition.required", "Workflow definition is required."));
            return new WorkflowDefinitionValidationResult(errors);
        }

        if (!string.Equals(document.Schema, DefaultWorkflowDefinitions.V1Alpha1Schema, StringComparison.Ordinal))
        {
            errors.Add(new WorkflowDefinitionValidationError(
                "definition.schema.unsupported",
                $"Schema '{document.Schema}' is not supported.",
                "schema"));
        }

        if (document.Steps.Count == 0)
        {
            errors.Add(new WorkflowDefinitionValidationError("definition.steps.required", "At least one step is required.", "steps"));
            return new WorkflowDefinitionValidationResult(errors);
        }

        ValidateTriggers(document.Triggers, errors);

        var stepsById = new Dictionary<string, WorkflowDefinitionStep>(StringComparer.Ordinal);
        var duplicateIds = document.Steps
            .GroupBy(step => step.Id, StringComparer.Ordinal)
            .Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        foreach (var duplicateId in duplicateIds)
        {
            errors.Add(new WorkflowDefinitionValidationError(
                "definition.step.id.duplicate",
                string.IsNullOrWhiteSpace(duplicateId) ? "Step id is required." : $"Step id '{duplicateId}' must be unique.",
                "steps[].id"));
        }

        foreach (var step in document.Steps.Where(step => !string.IsNullOrWhiteSpace(step.Id)))
        {
            stepsById.TryAdd(step.Id, step);
        }

        if (string.IsNullOrWhiteSpace(document.StartStepId) || !stepsById.ContainsKey(document.StartStepId))
        {
            errors.Add(new WorkflowDefinitionValidationError(
                "definition.startStepId.invalid",
                $"Start step id '{document.StartStepId}' does not reference an existing step.",
                "startStepId"));
        }

        foreach (var step in document.Steps)
        {
            if (!SupportedBuiltins.ContainsKey(step.Uses))
            {
                errors.Add(new WorkflowDefinitionValidationError(
                    "definition.step.uses.unsupported",
                    $"Step '{step.Id}' uses unsupported task '{step.Uses}'.",
                    "steps[].uses"));
            }

            if (!string.IsNullOrWhiteSpace(step.NextStepId) && !stepsById.ContainsKey(step.NextStepId))
            {
                errors.Add(new WorkflowDefinitionValidationError(
                    "definition.step.nextStepId.unknown",
                    $"Step '{step.Id}' references unknown next step '{step.NextStepId}'.",
                    "steps[].nextStepId"));
            }
        }

        if (errors.Count > 0 || !stepsById.TryGetValue(document.StartStepId, out var current))
        {
            return new WorkflowDefinitionValidationResult(errors);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (!visited.Add(current.Id))
            {
                errors.Add(new WorkflowDefinitionValidationError(
                    "definition.graph.cycle",
                    $"Sequential graph contains a cycle at step '{current.Id}'.",
                    "steps[].nextStepId"));
                break;
            }

            if (string.IsNullOrWhiteSpace(current.NextStepId))
            {
                break;
            }

            current = stepsById[current.NextStepId];
        }

        var disconnected = stepsById.Keys.Except(visited, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var stepId in disconnected)
        {
            errors.Add(new WorkflowDefinitionValidationError(
                "definition.graph.disconnected",
                $"Step '{stepId}' is not reachable from the start step.",
                "steps"));
        }

        var terminalCount = document.Steps.Count(step => string.IsNullOrWhiteSpace(step.NextStepId));
        if (terminalCount != 1)
        {
            errors.Add(new WorkflowDefinitionValidationError(
                "definition.graph.terminal.invalid",
                "Exactly one terminal step is required.",
                "steps"));
        }

        return new WorkflowDefinitionValidationResult(errors);
    }

    private static void ValidateTriggers(
        IReadOnlyList<WorkflowDefinitionTrigger>? triggers,
        List<WorkflowDefinitionValidationError> errors)
    {
        if (triggers is null || triggers.Count == 0)
        {
            return;
        }

        var duplicateIds = triggers
            .GroupBy(trigger => trigger.Id, StringComparer.Ordinal)
            .Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        foreach (var duplicateId in duplicateIds)
        {
            errors.Add(new WorkflowDefinitionValidationError(
                "definition.trigger.id.duplicate",
                string.IsNullOrWhiteSpace(duplicateId) ? "Trigger id is required." : $"Trigger id '{duplicateId}' must be unique.",
                "triggers[].id"));
        }

        foreach (var trigger in triggers)
        {
            if (!Enum.IsDefined(trigger.Type))
            {
                errors.Add(new WorkflowDefinitionValidationError(
                    "definition.trigger.type.unsupported",
                    $"Trigger '{trigger.Id}' uses unsupported type '{trigger.Type}'.",
                    "triggers[].type"));
            }

            if (!trigger.Enabled || trigger.Type != WorkflowTriggerType.DevOpsIssueLabel)
            {
                continue;
            }

            if (trigger.RepositoryIds.Count == 0)
            {
                errors.Add(new WorkflowDefinitionValidationError(
                    "definition.trigger.repositories.required",
                    $"Trigger '{trigger.Id}' requires at least one repository.",
                    "triggers[].repositoryIds"));
            }

            if (string.IsNullOrWhiteSpace(trigger.Label))
            {
                errors.Add(new WorkflowDefinitionValidationError(
                    "definition.trigger.label.required",
                    $"Trigger '{trigger.Id}' requires a label.",
                    "triggers[].label"));
            }
        }
    }

    public static bool TryMapUsesToTaskKind(string uses, out TaskRunKind kind)
        => SupportedBuiltins.TryGetValue(uses, out kind);

    public static string UsesFor(TaskRunKind kind)
        => kind switch
        {
            TaskRunKind.Plan => "builtins.plan",
            TaskRunKind.Implement => "builtins.implement",
            TaskRunKind.CreatePullRequest => "builtins.create-pull-request",
            TaskRunKind.AddressComments => "builtins.address-comments",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported task run kind.")
        };
}
