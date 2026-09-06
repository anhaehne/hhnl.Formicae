using System.Globalization;
using System.Text.Json;

namespace hhnl.Formicae.Application.Workflows;

public sealed record DecisionEvaluation(bool Result, string InputJson, Guid? SourceTaskRunId);

/// <summary>Evaluates typed scalar conditions without expressions, reflection or external input.</summary>
public static class WorkflowDecisionEvaluator
{
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
        { "issueUrl", "repositoryUrl", "baseBranch", "model", "planArtifact", "pullRequestUrl" };

    public static WorkflowDefinitionValidationResult Validate(WorkflowDecisionCondition? condition)
    {
        var errors = new List<WorkflowDefinitionValidationError>();
        void Error(string field, string message) => errors.Add(new("definition.decision.condition.invalid", message, $"decision.condition.{field}"));
        if (condition is null)
        {
            Error("source", "Decision condition is required.");
            return new(errors);
        }
        if (condition.Source is not ("literal" or "workflowField" or "taskOutput"))
            Error("source", "Condition source must be literal, workflowField or taskOutput.");
        if (condition.ValueType is not ("string" or "number" or "boolean"))
            Error("valueType", "Condition value type must be string, number or boolean.");
        if (condition.Operator is not ("equals" or "notEquals" or "contains" or "exists" or "greaterThan" or "greaterThanOrEqual" or "lessThan" or "lessThanOrEqual"))
            Error("operator", "Condition operator is not supported.");
        else if (condition.Operator == "contains" && condition.ValueType != "string")
            Error("operator", "Contains requires string values.");
        else if (condition.Operator is "greaterThan" or "greaterThanOrEqual" or "lessThan" or "lessThanOrEqual" && condition.ValueType != "number")
            Error("operator", "Ordering operators require number values.");
        if (condition.MissingValue is not ("error" or "false"))
            Error("missingValue", "Missing value behavior must be error or false.");

        if (condition.Source == "literal")
        {
            if (condition.Reference is not null) Error("reference", "Literal sources cannot have a reference.");
            // Nullable JsonElement treats omitted and explicit JSON null alike after persistence.
            if (condition.Value is { } literal && literal.ValueKind != JsonValueKind.Null && !Matches(literal, condition.ValueType))
                Error("value", "Literal source value must match the configured scalar type and numeric range.");
        }
        else if (condition.Source is "workflowField" or "taskOutput")
        {
            if (condition.Value is not null) Error("value", "Referenced sources cannot also define a literal value.");
            if (condition.Source == "workflowField" && !Fields.Contains(condition.Reference ?? ""))
                Error("reference", "Workflow field is not supported.");
            if (condition.Source == "taskOutput" && string.IsNullOrWhiteSpace(condition.Reference))
                Error("reference", "Task output requires an explicit source step ID.");
        }
        if (condition.Operator == "exists")
        {
            if (condition.CompareTo is not null) Error("compareTo", "Exists does not accept a comparison value.");
        }
        else if (condition.CompareTo is null || !Matches(condition.CompareTo.Value, condition.ValueType))
            Error("compareTo", "Comparison value must match the configured scalar type and numeric range.");
        return new(errors);
    }

    public static DecisionEvaluation Evaluate(WorkflowDecisionCondition condition, Workflow workflow, TaskRun? sourceRun = null)
    {
        var validation = Validate(condition);
        if (!validation.IsValid) throw new InvalidOperationException(string.Join(" ", validation.Errors.Select(error => error.Message)));
        object? value;
        Guid? sourceTaskRunId = null;
        if (condition.Source == "literal") value = condition.Value is { } literal ? Scalar(literal) : null;
        else if (condition.Source == "workflowField") value = condition.Reference switch
        {
            "issueUrl" => workflow.IssueUrl,
            "repositoryUrl" => workflow.RepositoryUrl,
            "baseBranch" => workflow.BaseBranch,
            "model" => workflow.Model,
            "planArtifact" => workflow.PlanArtifact,
            "pullRequestUrl" => workflow.PullRequestUrl,
            _ => throw new InvalidOperationException("Unsupported workflow field.")
        };
        else
        {
            if (sourceRun is not null && (sourceRun.WorkflowId != workflow.Id || sourceRun.DefinitionStepId != condition.Reference
                || sourceRun.Status != TaskRunStatus.Succeeded || sourceRun.LoopIteration is not null))
                throw new InvalidOperationException("Decision source must be the succeeded, non-loop execution of the configured task in this workflow.");
            value = sourceRun?.Output;
            sourceTaskRunId = sourceRun?.Id;
        }

        var missing = value is null;
        bool result;
        if (condition.Operator == "exists") result = !missing;
        else if (missing)
        {
            if (condition.MissingValue == "error") throw new InvalidOperationException("Decision input is missing.");
            result = false;
        }
        else
        {
            if (condition.Source != "literal") value = ParseText((string)value!, condition.ValueType);
            var comparison = Scalar(condition.CompareTo!.Value)!;
            var order = value switch
            {
                string text => string.Compare(text, (string)comparison, StringComparison.Ordinal),
                decimal number => number.CompareTo((decimal)comparison),
                bool boolean => boolean.CompareTo((bool)comparison),
                _ => throw new InvalidOperationException("Unsupported decision scalar.")
            };
            result = condition.Operator switch
            {
                "equals" => order == 0,
                "notEquals" => order != 0,
                "contains" => ((string)value!).Contains((string)comparison, StringComparison.Ordinal),
                "greaterThan" => order > 0,
                "greaterThanOrEqual" => order >= 0,
                "lessThan" => order < 0,
                "lessThanOrEqual" => order <= 0,
                _ => throw new InvalidOperationException("Unsupported decision operator.")
            };
        }
        var input = JsonSerializer.Serialize(new
        {
            source = condition.Source, reference = condition.Reference, valueType = condition.ValueType,
            resolvedType = value switch { string => "string", decimal => "number", bool => "boolean", _ => (string?)null },
            value, missing, sourceTaskRunId
        });
        return new(result, input, sourceTaskRunId);
    }

    private static bool Matches(JsonElement value, string type) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        _ => false
    };

    private static object? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidOperationException("Decision values must be JSON scalars.")
    };

    private static object ParseText(string text, string type) => type switch
    {
        "string" => text,
        "number" when decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        "boolean" when text.Trim() == "true" => true,
        "boolean" when text.Trim() == "false" => false,
        _ => throw new InvalidOperationException($"Decision input is not a valid {type} value.")
    };
}
