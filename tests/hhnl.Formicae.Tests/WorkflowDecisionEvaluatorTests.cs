using System.Globalization;
using System.Text.Json;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowDecisionEvaluatorTests
{
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
    private static Workflow Workflow() => new() { IssueUrl = "https://example.com/issues/1", RepositoryUrl = "https://example.com/repo" };
    private static WorkflowDecisionCondition Literal(string type, string operation, string value, string? comparison = null)
        => new("literal", type, operation, Value: Json(value), CompareTo: comparison is null ? null : Json(comparison));
    private static WorkflowDecisionCondition Output(string type, string operation, string? comparison = null)
        => new("taskOutput", type, operation, Reference: "source", CompareTo: comparison is null ? null : Json(comparison));
    private static TaskRun Run(Workflow workflow, string? output) => new()
    { WorkflowId = workflow.Id, DefinitionStepId = "source", Kind = TaskRunKind.Plan, Status = TaskRunStatus.Succeeded, Output = output };

    [Theory]
    [InlineData("string", "equals", "\"Alpha\"", "\"Alpha\"", true)]
    [InlineData("string", "equals", "\"Alpha\"", "\"alpha\"", false)]
    [InlineData("string", "notEquals", "\"Alpha\"", "\"alpha\"", true)]
    [InlineData("string", "notEquals", "\"Alpha\"", "\"Alpha\"", false)]
    [InlineData("string", "contains", "\"Alpha Beta\"", "\"Beta\"", true)]
    [InlineData("string", "contains", "\"Alpha Beta\"", "\"beta\"", false)]
    [InlineData("string", "contains", "\"\"", "\"\"", true)]
    [InlineData("boolean", "equals", "true", "true", true)]
    [InlineData("boolean", "equals", "false", "true", false)]
    [InlineData("boolean", "notEquals", "false", "true", true)]
    [InlineData("boolean", "notEquals", "true", "true", false)]
    [InlineData("number", "equals", "1.00", "1", true)]
    [InlineData("number", "notEquals", "1", "2", true)]
    [InlineData("number", "greaterThan", "10", "2", true)]
    [InlineData("number", "greaterThan", "2", "2", false)]
    [InlineData("number", "greaterThanOrEqual", "2", "2", true)]
    [InlineData("number", "greaterThanOrEqual", "1", "2", false)]
    [InlineData("number", "lessThan", "-1", "0", true)]
    [InlineData("number", "lessThan", "0", "0", false)]
    [InlineData("number", "lessThanOrEqual", "0", "0", true)]
    [InlineData("number", "lessThanOrEqual", "1", "0", false)]
    public void Literal_operators_use_explicit_scalar_semantics(string type, string operation, string value, string comparison, bool expected)
    {
        var condition = Literal(type, operation, value, comparison);
        Assert.True(WorkflowDecisionEvaluator.Validate(condition).IsValid);
        Assert.Equal(expected, WorkflowDecisionEvaluator.Evaluate(condition, Workflow()).Result);
    }

    [Theory]
    [InlineData("string", "\"\"", true)]
    [InlineData("string", "null", false)]
    [InlineData("number", "0", true)]
    [InlineData("number", "null", false)]
    [InlineData("boolean", "false", true)]
    [InlineData("boolean", "null", false)]
    public void Exists_distinguishes_missing_from_empty_zero_and_false(string type, string value, bool expected)
        => Assert.Equal(expected, WorkflowDecisionEvaluator.Evaluate(Literal(type, "exists", value), Workflow()).Result);

    [Theory]
    [InlineData("exists")]
    [InlineData("equals")]
    public void Missing_literal_semantics_survive_json_round_trip(string operation)
    {
        var condition = Literal("string", operation, "null", operation == "exists" ? null : "\"value\"") with { MissingValue = "false" };
        var persisted = JsonSerializer.Deserialize<WorkflowDecisionCondition>(JsonSerializer.Serialize(condition))!;
        Assert.True(WorkflowDecisionEvaluator.Validate(persisted).IsValid);
        Assert.False(WorkflowDecisionEvaluator.Evaluate(persisted, Workflow()).Result);
        Assert.Equal(WorkflowDecisionEvaluator.Evaluate(condition, Workflow()).InputJson,
            WorkflowDecisionEvaluator.Evaluate(persisted, Workflow()).InputJson);
    }

    [Theory]
    [InlineData("number", "not a number")]
    [InlineData("boolean", "not a boolean")]
    [InlineData("string", "")]
    public void Exists_checks_presence_without_converting_text(string type, string text)
    {
        var workflow = Workflow();
        var evaluation = WorkflowDecisionEvaluator.Evaluate(Output(type, "exists"), workflow, Run(workflow, text));
        Assert.True(evaluation.Result);
        using var input = JsonDocument.Parse(evaluation.InputJson);
        Assert.Equal("string", input.RootElement.GetProperty("resolvedType").GetString());
        Assert.Equal(text, input.RootElement.GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("equals")]
    [InlineData("notEquals")]
    [InlineData("contains")]
    public void Missing_input_false_policy_never_selects_true_even_for_not_equals(string operation)
    {
        var evaluation = WorkflowDecisionEvaluator.Evaluate(Output("string", operation, "\"anything\"") with { MissingValue = "false" }, Workflow());
        Assert.False(evaluation.Result);
        using var input = JsonDocument.Parse(evaluation.InputJson);
        Assert.True(input.RootElement.GetProperty("missing").GetBoolean());
        Assert.Null(evaluation.SourceTaskRunId);
    }

    [Fact]
    public void Missing_input_errors_by_default()
        => Assert.Throws<InvalidOperationException>(() => WorkflowDecisionEvaluator.Evaluate(Output("string", "equals", "\"value\""), Workflow()));

    [Theory]
    [InlineData("number", " 1.25e2 ", "125")]
    [InlineData("number", "-0.5", "-0.5")]
    [InlineData("boolean", "true\n", "true")]
    [InlineData("boolean", "false", "false")]
    [InlineData("string", "  text\n", "\"  text\\n\"")]
    public void Task_output_uses_explicit_type_and_audits_resolved_value(string type, string output, string comparison)
    {
        var workflow = Workflow();
        var run = Run(workflow, output);
        var evaluation = WorkflowDecisionEvaluator.Evaluate(Output(type, "equals", comparison), workflow, run);
        Assert.True(evaluation.Result);
        Assert.Equal(run.Id, evaluation.SourceTaskRunId);
        using var input = JsonDocument.Parse(evaluation.InputJson);
        Assert.Equal(type, input.RootElement.GetProperty("resolvedType").GetString());
        Assert.Equal(run.Id, input.RootElement.GetProperty("sourceTaskRunId").GetGuid());
        Assert.Equal("source", input.RootElement.GetProperty("reference").GetString());
    }

    [Theory]
    [InlineData("number", "NaN", "0")]
    [InlineData("number", "Infinity", "0")]
    [InlineData("number", "1,25", "0")]
    [InlineData("number", "1,000", "0")]
    [InlineData("number", "1e100", "0")]
    [InlineData("number", "", "0")]
    [InlineData("number", "true", "0")]
    [InlineData("boolean", "1", "true")]
    [InlineData("boolean", "yes", "true")]
    [InlineData("boolean", "True", "true")]
    [InlineData("boolean", "", "true")]
    public void Invalid_typed_output_is_an_error_even_with_missing_false_policy(string type, string output, string comparison)
    {
        var workflow = Workflow();
        Assert.Throws<InvalidOperationException>(() => WorkflowDecisionEvaluator.Evaluate(
            Output(type, "equals", comparison) with { MissingValue = "false" }, workflow, Run(workflow, output)));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public void Numeric_and_string_matching_are_culture_independent(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var workflow = Workflow();
            Assert.True(WorkflowDecisionEvaluator.Evaluate(Output("number", "equals", "1.5"), workflow, Run(workflow, "1.5")).Result);
            Assert.False(WorkflowDecisionEvaluator.Evaluate(Literal("string", "equals", "\"I\"", "\"ı\""), workflow).Result);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("issueUrl", "https://example.com/issues/1")]
    [InlineData("repositoryUrl", "https://example.com/repo")]
    [InlineData("baseBranch", "main")]
    [InlineData("model", "chosen-model")]
    [InlineData("planArtifact", "plan text")]
    [InlineData("pullRequestUrl", "https://example.com/pr/1")]
    public void Only_selected_workflow_field_is_captured(string field, string expected)
    {
        var workflow = Workflow();
        workflow.Model = "chosen-model";
        workflow.PlanArtifact = "plan text";
        workflow.PullRequestUrl = "https://example.com/pr/1";
        workflow.FailureReason = "unrelated sensitive diagnostic";
        var condition = new WorkflowDecisionCondition("workflowField", "string", "equals", Reference: field, CompareTo: JsonSerializer.SerializeToElement(expected));
        var evaluation = WorkflowDecisionEvaluator.Evaluate(condition, workflow);
        Assert.True(evaluation.Result);
        Assert.DoesNotContain(workflow.FailureReason, evaluation.InputJson);
        Assert.Null(evaluation.SourceTaskRunId);
    }

    [Fact]
    public void Missing_workflow_field_and_null_task_output_are_audited_as_missing()
    {
        var workflow = Workflow();
        Assert.False(WorkflowDecisionEvaluator.Evaluate(new("workflowField", "string", "exists", Reference: "model"), workflow).Result);
        var run = Run(workflow, null);
        var evaluation = WorkflowDecisionEvaluator.Evaluate(Output("string", "exists"), workflow, run);
        Assert.False(evaluation.Result);
        Assert.Equal(run.Id, evaluation.SourceTaskRunId);
    }

    [Fact]
    public void Mismatched_or_unsuccessful_task_execution_is_rejected()
    {
        var workflow = Workflow();
        var cases = new[]
        {
            Run(Workflow(), "value"),
            new TaskRun { WorkflowId = workflow.Id, DefinitionStepId = "wrong", Status = TaskRunStatus.Succeeded, Output = "value" },
            new TaskRun { WorkflowId = workflow.Id, DefinitionStepId = "source", Status = TaskRunStatus.Failed, Output = "value" },
            new TaskRun { WorkflowId = workflow.Id, DefinitionStepId = "source", Status = TaskRunStatus.Succeeded, LoopIteration = 1, Output = "value" }
        };
        foreach (var run in cases)
            Assert.Throws<InvalidOperationException>(() => WorkflowDecisionEvaluator.Evaluate(Output("string", "exists"), workflow, run));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("type")]
    [InlineData("operator")]
    [InlineData("missing")]
    [InlineData("literal-reference")]
    [InlineData("object")]
    [InlineData("array")]
    [InlineData("mismatched-source")]
    [InlineData("mismatched-comparison")]
    [InlineData("null-comparison")]
    [InlineData("missing-comparison")]
    [InlineData("numeric-overflow")]
    [InlineData("numeric-string")]
    [InlineData("boolean-contains")]
    [InlineData("string-ordering")]
    [InlineData("unknown-field")]
    [InlineData("task-reference")]
    [InlineData("referenced-literal")]
    [InlineData("exists-comparison")]
    public void Invalid_or_ambiguous_conditions_fail_validation(string scenario)
    {
        var valid = Literal("string", "equals", "\"value\"", "\"value\"");
        var condition = scenario switch
        {
            "source" => valid with { Source = "expression" },
            "type" => valid with { ValueType = "object" },
            "operator" => valid with { Operator = "regex" },
            "missing" => valid with { MissingValue = "true" },
            "literal-reference" => valid with { Reference = "model" },
            "object" => valid with { Value = Json("{}") },
            "array" => valid with { Value = Json("[]") },
            "mismatched-source" => valid with { Value = Json("true") },
            "mismatched-comparison" => valid with { CompareTo = Json("true") },
            "null-comparison" => valid with { CompareTo = Json("null") },
            "missing-comparison" => valid with { CompareTo = null },
            "numeric-overflow" => Literal("number", "equals", "1e100", "0"),
            "numeric-string" => Literal("number", "equals", "\"1\"", "1"),
            "boolean-contains" => Literal("boolean", "contains", "true", "false"),
            "string-ordering" => valid with { Operator = "greaterThan" },
            "unknown-field" => new("workflowField", "string", "exists", Reference: "FailureReason"),
            "task-reference" => new("taskOutput", "string", "exists", Reference: " "),
            "referenced-literal" => new("workflowField", "string", "exists", Reference: "model", Value: Json("\"oops\"")),
            _ => valid with { Operator = "exists" }
        };
        var validation = WorkflowDecisionEvaluator.Validate(condition);
        Assert.False(validation.IsValid);
        Assert.All(validation.Errors, error => Assert.StartsWith("decision.condition.", error.Path));
        Assert.Throws<InvalidOperationException>(() => WorkflowDecisionEvaluator.Evaluate(condition, Workflow()));
    }

    [Fact]
    public void Missing_condition_returns_validation_error_instead_of_throwing()
        => Assert.False(WorkflowDecisionEvaluator.Validate(null).IsValid);
}
