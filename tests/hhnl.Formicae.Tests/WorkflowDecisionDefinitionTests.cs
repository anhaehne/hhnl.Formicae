using System.Text.Json;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowDecisionDefinitionTests
{
    private static WorkflowDecisionCondition Condition() => new("literal", "boolean", "equals",
        Value: JsonSerializer.SerializeToElement(true), CompareTo: JsonSerializer.SerializeToElement(true));
    private static WorkflowDefinitionStep Decision(string id, string yes, string no) => new(id, WorkflowDecisionDefinitions.Uses,
        Decision: new(Condition(), yes, no));
    private static WorkflowDefinitionStep Trigger(string id, string next) => new(id, WorkflowNodeDefinitions.TriggerUses, next,
        Trigger: new(WorkflowTriggerType.Manual, true, [], null));
    private static WorkflowDefinitionDocument Document() => new(DefaultWorkflowDefinitions.V1Alpha3Schema, "decision",
        [Decision("decision", "yes", "no"), new("yes", "builtins.plan", "finish"), new("no", "builtins.plan", "finish"), new("finish", "builtins.implement")]);
    private static WorkflowDefinitionDocument Change(WorkflowDefinitionDocument document, string id, Func<WorkflowDefinitionStep, WorkflowDefinitionStep> change)
        => document with { Steps = document.Steps.Select(step => step.Id == id ? change(step) : step).ToArray() };
    private static WorkflowDefinitionDocument Source(WorkflowDefinitionDocument document, string decision, string source)
        => Change(document, decision, node => node with { Decision = node.Decision! with
        { Condition = new("taskOutput", "string", "exists", Reference: source) } });
    private static void Valid(WorkflowDefinitionDocument document)
    {
        var result = new WorkflowDefinitionValidator().Validate(document);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(error => error.Message)));
    }
    private static void Invalid(WorkflowDefinitionDocument document, string? nodeId = null)
    {
        var result = new WorkflowDefinitionValidator().Validate(document);
        Assert.False(result.IsValid);
        if (nodeId is not null) Assert.Contains(result.Errors, error => error.NodeId == nodeId);
    }

    [Fact]
    public void Exclusive_arms_can_converge_at_one_shared_task() => Valid(Document());

    [Theory]
    [InlineData("formicae.workflow/v1alpha1")]
    [InlineData("formicae.workflow/v1alpha2")]
    public void Legacy_task_cannot_smuggle_unvalidated_decision_settings(string schema)
    {
        var document = Document() with { Schema = schema };
        document = Change(document, "decision", node => node with { Uses = "builtins.plan", NextStepId = "yes" });
        var result = new WorkflowDefinitionValidator().Validate(document);
        Assert.Contains(result.Errors, error => error.Code == "definition.decision.schema.required");
    }

    [Fact]
    public void Exclusive_arms_can_end_independently()
        => Valid(Document() with { Steps = [Decision("decision", "yes", "no"), new("yes", "builtins.plan"), new("no", "builtins.implement")] });

    [Fact]
    public void Nested_decisions_and_convergence_form_valid_outer_dag()
    {
        var document = Change(Document(), "yes", _ => Decision("yes", "inner-yes", "inner-no"));
        Valid(document with { Steps = [.. document.Steps, new("inner-yes", "builtins.plan", "finish"), new("inner-no", "builtins.plan", "finish")] });
    }

    [Theory]
    [InlineData("loop")]
    [InlineData("parallel")]
    public void Control_group_on_one_arm_is_valid(string kind)
    {
        var document = GroupOnArm(kind);
        Valid(document);
        var normalized = WorkflowNodeDefinitions.Normalize(document);
        var decision = normalized.Steps.Single(step => step.Id == "decision").Decision!;
        Assert.Equal("group", decision.ConfiguredTrueStepId);
        Assert.Equal(kind == "loop" ? "body-a" : "group", decision.TrueStepId);
        Assert.Equal("no", decision.FalseStepId);
        if (kind == "loop") Assert.Equal("finish", Assert.Single(normalized.Loops!).ExitStepId);
        else Assert.Equal("join", normalized.Steps.Single(step => step.Id == "body-a").NextStepPort);
    }

    [Fact]
    public void Ordinary_task_must_dominate_decision_across_manual_and_trigger_entries()
    {
        var document = Source(Document(), "decision", "source") with { StartStepId = "source" };
        document = document with { Steps = [.. document.Steps, new("source", "builtins.plan", "decision"), Trigger("event", "source")] };
        Valid(document);
        Invalid(Change(document, "event", step => step with { NextStepId = "decision" }), "decision");
    }

    [Fact]
    public void Shared_task_after_exclusive_convergence_can_supply_later_decision()
    {
        var document = Change(Document(), "finish", step => step with { NextStepId = "later" });
        document = document with { Steps = [.. document.Steps, Decision("later", "end-yes", "end-no"), new("end-yes", "builtins.plan"), new("end-no", "builtins.plan")] };
        Valid(Source(document, "later", "finish"));
        Invalid(Source(document, "later", "yes"), "later");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("decision")]
    [InlineData("yes")]
    [InlineData("finish")]
    public void Missing_control_future_or_conditional_output_sources_are_rejected(string source)
        => Invalid(Source(Document(), "decision", source), "decision");

    [Theory]
    [InlineData("loop")]
    [InlineData("parallel")]
    public void Output_from_control_body_is_rejected_even_after_group_completion(string kind)
    {
        var document = GroupOnArm(kind);
        document = Change(document, "finish", _ => Decision("finish", "end-yes", "end-no"));
        document = document with { Steps = [.. document.Steps, new("end-yes", "builtins.plan"), new("end-no", "builtins.plan")] };
        Invalid(Source(document, "finish", "body-a"), "finish");
    }

    [Theory]
    [InlineData("loop")]
    [InlineData("parallel")]
    public void Decision_cannot_enter_control_body_directly(string kind)
    {
        var document = GroupOnArm(kind);
        Invalid(Change(document, "decision", node => node with { Decision = node.Decision! with { FalseStepId = "body-a" } }), "decision");
    }

    [Theory]
    [InlineData("loop")]
    [InlineData("parallel")]
    public void Decision_cannot_be_nested_inside_control_body(string kind)
    {
        var document = GroupOnArm(kind);
        Invalid(Change(document, "body-a", _ => Decision("body-a", "no", "finish")), "group");
    }

    [Theory]
    [InlineData("loop")]
    [InlineData("parallel")]
    public void Body_cannot_escape_into_exclusive_arm(string kind)
        => Invalid(Change(GroupOnArm(kind), "body-a", step => step with { NextStepId = "no", NextStepPort = null }), "group");

    [Theory]
    [InlineData("loop")]
    [InlineData("parallel")]
    public void Manual_start_cannot_enter_body(string kind)
        => Invalid(GroupOnArm(kind) with { StartStepId = "body-a" }, "body-a");

    [Fact]
    public void Trigger_can_start_decision_with_literal_condition()
    {
        var document = Document();
        Valid(document with { Steps = [.. document.Steps, Trigger("event", "decision")] });
    }

    [Theory]
    [InlineData("decision")]
    [InlineData("yes")]
    public void Outer_cycles_are_rejected(string target)
        => Invalid(Change(Document(), "finish", node => node with { NextStepId = target }));

    [Fact]
    public void Disconnected_task_is_rejected()
    {
        var document = Document();
        Invalid(document with { Steps = [.. document.Steps, new("unused", "builtins.plan")] }, "unused");
    }

    [Theory]
    [InlineData("same-target")]
    [InlineData("missing-target")]
    [InlineData("missing-settings")]
    [InlineData("next")]
    [InlineData("join")]
    [InlineData("model")]
    [InlineData("ai")]
    [InlineData("loop-settings")]
    [InlineData("bad-condition")]
    [InlineData("null-condition")]
    public void Malformed_decision_settings_are_located_on_decision(string scenario)
    {
        Invalid(Change(Document(), "decision", node => scenario switch
        {
            "same-target" => node with { Decision = node.Decision! with { FalseStepId = "yes" } },
            "missing-target" => node with { Decision = node.Decision! with { TrueStepId = "missing" } },
            "missing-settings" => node with { Decision = null },
            "next" => node with { NextStepId = "finish" },
            "join" => node with { NextStepPort = "join" },
            "model" => node with { Model = "model" },
            "ai" => node with { AiSettingsId = "agent" },
            "loop-settings" => node with { Loop = new("yes", 2, 2) },
            "null-condition" => node with { Decision = node.Decision! with { Condition = null! } },
            _ => node with { Decision = node.Decision! with { Condition = Condition() with { Operator = "script" } } }
        }), "decision");
    }

    [Fact]
    public void Decision_settings_on_task_are_rejected()
        => Invalid(Change(Document(), "yes", node => node with { Decision = new(Condition(), "no", "finish") }), "yes");

    [Fact]
    public void Normalization_preserves_exclusive_targets_and_json_omits_internal_targets()
    {
        var document = GroupOnArm("loop");
        var normalized = WorkflowNodeDefinitions.Normalize(document);
        var json = WorkflowDefinitionJson.Serialize(normalized);
        Assert.DoesNotContain("configuredTrueStepId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("configuredFalseStepId", json, StringComparison.OrdinalIgnoreCase);
        var persisted = WorkflowDefinitionJson.Deserialize(WorkflowDefinitionJson.Serialize(document))!;
        Valid(persisted);
        Assert.Equal("group", persisted.Steps.Single(step => step.Id == "decision").Decision!.TrueStepId);
    }

    private static WorkflowDefinitionDocument GroupOnArm(string kind)
    {
        var document = Document();
        document = Change(document, "decision", node => node with { Decision = node.Decision! with { TrueStepId = "group" } });
        var steps = document.Steps.Where(step => step.Id != "yes").ToArray();
        return document with { Steps = kind == "loop"
            ? [.. steps, new("group", WorkflowNodeDefinitions.LoopUses, "finish", Loop: new("body-a", 2, 2)),
                new("body-a", "builtins.plan", "group", NextStepPort: "return")]
            : [.. steps, new("group", WorkflowParallelDefinitions.Uses, "finish", Parallel: new(["body-a", "body-b"])),
                new("body-a", "builtins.plan", "group", NextStepPort: "join"), new("body-b", "builtins.plan", "group", NextStepPort: "join")] };
    }
}
