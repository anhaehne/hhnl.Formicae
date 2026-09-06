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

        if (!string.Equals(document.Schema, DefaultWorkflowDefinitions.V1Alpha1Schema, StringComparison.Ordinal)
            && !string.Equals(document.Schema, DefaultWorkflowDefinitions.V1Alpha3Schema, StringComparison.Ordinal)
            && !string.Equals(document.Schema, DefaultWorkflowDefinitions.V1Alpha2Schema, StringComparison.Ordinal))
        {
            errors.Add(new WorkflowDefinitionValidationError(
                "definition.schema.unsupported",
                $"Schema '{document.Schema}' is not supported.",
                "schema"));
        }

        if (document.Steps is null) return new([new("definition.steps.required", "At least one step is required.", "steps")]);

        if (document.Schema == DefaultWorkflowDefinitions.V1Alpha3Schema)
            return WorkflowNodeDefinitions.Validate(document);

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

        var loops = document.Loops ?? [];
        if (loops.Count > 0 && !string.Equals(document.Schema, DefaultWorkflowDefinitions.V1Alpha2Schema, StringComparison.Ordinal))
        {
            errors.Add(new WorkflowDefinitionValidationError(
                "definition.loops.schema.required",
                $"Loops require schema '{DefaultWorkflowDefinitions.V1Alpha2Schema}'.",
                "schema"));
        }

        var loopIds = new HashSet<string>(StringComparer.Ordinal);
        var loopByBodyStep = new Dictionary<string, WorkflowDefinitionLoop>(StringComparer.Ordinal);
        foreach (var loop in loops)
        {
            if (string.IsNullOrWhiteSpace(loop.Id) || !loopIds.Add(loop.Id))
            {
                errors.Add(new WorkflowDefinitionValidationError("definition.loop.id.invalid", "Loop ids are required and must be unique.", "loops[].id"));
            }
            if (loop.BodyStepIds.Count == 0)
            {
                errors.Add(new WorkflowDefinitionValidationError("definition.loop.body.required", $"Loop '{loop.Id}' requires at least one body step.", "loops[].bodyStepIds", loop.Id));
                continue;
            }
            if (loop.RepeatCount <= 0 || loop.MaxIterations <= 0 || loop.RepeatCount > loop.MaxIterations)
            {
                errors.Add(new WorkflowDefinitionValidationError("definition.loop.bounds.invalid", $"Loop '{loop.Id}' requires positive bounds with repeatCount less than or equal to maxIterations.", "loops", loop.Id));
            }
            if (loop.TimeoutSeconds is <= 0)
            {
                errors.Add(new WorkflowDefinitionValidationError("definition.loop.timeout.invalid", $"Loop '{loop.Id}' timeoutSeconds must be positive when provided.", "loops[].timeoutSeconds", loop.Id));
            }
            if (!stepsById.ContainsKey(loop.ExitStepId) || loop.BodyStepIds.Contains(loop.ExitStepId, StringComparer.Ordinal))
            {
                errors.Add(new WorkflowDefinitionValidationError("definition.loop.exit.invalid", $"Loop '{loop.Id}' exit step '{loop.ExitStepId}' must reference a step outside its body.", "loops[].exitStepId", loop.Id));
            }
            for (var index = 0; index < loop.BodyStepIds.Count; index++)
            {
                var stepId = loop.BodyStepIds[index];
                if (!stepsById.TryGetValue(stepId, out var bodyStep))
                {
                    errors.Add(new WorkflowDefinitionValidationError("definition.loop.body.unknown", $"Loop '{loop.Id}' references unknown body step '{stepId}'.", "loops[].bodyStepIds", loop.Id));
                    continue;
                }
                if (!loopByBodyStep.TryAdd(stepId, loop))
                {
                    errors.Add(new WorkflowDefinitionValidationError("definition.loop.body.overlap", $"Step '{stepId}' belongs to more than one loop.", "loops[].bodyStepIds"));
                }
                var expectedNext = index + 1 < loop.BodyStepIds.Count ? loop.BodyStepIds[index + 1] : loop.BodyStepIds[0];
                if (!string.Equals(bodyStep.NextStepId, expectedNext, StringComparison.Ordinal))
                {
                    errors.Add(new WorkflowDefinitionValidationError("definition.loop.transition.invalid", $"Loop '{loop.Id}' body must be contiguous and close with a back-edge to '{loop.BodyStepIds[0]}'.", "steps[].nextStepId", loop.Id));
                }
            }
        }

        if (errors.Count > 0 || !stepsById.TryGetValue(document.StartStepId, out var current))
        {
            return new WorkflowDefinitionValidationResult(errors);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string stepId)
        {
            if (visiting.Contains(stepId))
            {
                errors.Add(new WorkflowDefinitionValidationError("definition.graph.cycle", $"Sequential graph contains an undeclared cycle at step '{stepId}'.", "steps[].nextStepId"));
                return;
            }
            if (!visited.Add(stepId)) return;
            visiting.Add(stepId);
            var step = stepsById[stepId];
            if (!string.IsNullOrWhiteSpace(step.NextStepId))
            {
                var isDeclaredBackEdge = loopByBodyStep.TryGetValue(step.Id, out var owner)
                    && string.Equals(owner.BodyStepIds[^1], step.Id, StringComparison.Ordinal)
                    && string.Equals(owner.BodyStepIds[0], step.NextStepId, StringComparison.Ordinal);
                if (!isDeclaredBackEdge) Visit(step.NextStepId);
                else Visit(owner!.ExitStepId);
            }
            visiting.Remove(stepId);
        }
        Visit(current.Id);

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
                    "triggers[].type", trigger.Id));
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
                    "triggers[].repositoryIds", trigger.Id));
            }

            if (string.IsNullOrWhiteSpace(trigger.Label))
            {
                errors.Add(new WorkflowDefinitionValidationError(
                    "definition.trigger.label.required",
                    $"Trigger '{trigger.Id}' requires a label.",
                    "triggers[].label", trigger.Id));
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
