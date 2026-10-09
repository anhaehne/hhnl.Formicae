using System.Text.Json;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowVariableTests
{
    private static JsonElement V<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static CustomTaskInputBinding B(string id) => new(id, "result");
    private static WorkflowDefinitionStep Producer(string id, string? next, string type = "string") => new(id, CustomTaskDefinitions.Uses, next,
        CustomTask: new(id, Snapshot: new(id, 1, id, "", "Produce", [], new(), [new("result", type)])));
    private static WorkflowDefinitionStep Consumer(string type = "string", string source = "combined") => new("consumer", CustomTaskDefinitions.Uses,
        CustomTask: new("consumer", Snapshot: new("consumer", 1, "Consumer", "", "Use {{input.result}}", [new("result", type, true)], new()),
            Bindings: new Dictionary<string, CustomTaskInputBinding> { ["result"] = new(source, "value") }));
    private static WorkflowDefinitionDocument Document(string type = "string") => new(DefaultWorkflowDefinitions.V1Alpha3Schema, "a",
        [Producer("a", "b", type), Producer("b", "consumer", type), Consumer(type)],
        Variables: [new("combined", "Combined", type, Sources: [B("a"), B("b")])]);

    [Theory]
    [InlineData("string", "aggregate", "any", "[\"a\",null,\"\",\"b\"]", "\"a\\n\\nb\"")]
    [InlineData("number", "aggregate", "any", "[3,null,0,7]", "10")]
    [InlineData("boolean", "aggregate", "any", "[false,null,true]", "true")]
    [InlineData("boolean", "aggregate", "all", "[true,null,false]", "false")]
    [InlineData("boolean", "aggregate", "all", "[true,true]", "true")]
    [InlineData("boolean", "first", "any", "[null,false,true]", "false")]
    [InlineData("number", "first", "any", "[null,0,2]", "0")]
    [InlineData("string", "first", "any", "[null,\"\",\"b\"]", "\"\"")]
    [InlineData("number", "override", "any", "[2,0,null]", "0")]
    [InlineData("boolean", "override", "any", "[true,false,null]", "false")]
    public void Combination_preserves_order_and_present_falsy_values(string type, string mode, string booleanOperation, string inputs, string expected)
    {
        var values = JsonSerializer.Deserialize<JsonElement[]>(inputs)!.Select(value => value.ValueKind == JsonValueKind.Null ? (JsonElement?)null : value);
        var result = WorkflowVariableDefinitions.Combine(new("v", "Value", type, mode, BooleanOperation: booleanOperation), values);
        Assert.Equal(expected, result!.Value.GetRawText());
    }

    [Theory]
    [InlineData("string")]
    [InlineData("number")]
    [InlineData("boolean")]
    public void Empty_sources_remain_absent(string type)
    {
        Assert.Null(WorkflowVariableDefinitions.Combine(new("v", "Value", type), []));
        Assert.Null(WorkflowVariableDefinitions.Combine(new("v", "Value", type), [null, null]));
    }

    [Fact]
    public void Separator_and_result_limits_are_enforced()
    {
        Assert.Equal("ab", WorkflowVariableDefinitions.Combine(new("v", "Value", "string", Separator: ""), [V("a"), V("b")])!.Value.GetString());
        Assert.Throws<InvalidOperationException>(() => WorkflowVariableDefinitions.Combine(new("v", "Value", "string"), [V(new string('x', 8000)), V(new string('x', 8000))]));
        Assert.Throws<InvalidOperationException>(() => WorkflowVariableDefinitions.Combine(new("v", "Value", "number"), [V(9007199254740991m), V(1)]));
        Assert.Throws<InvalidOperationException>(() => WorkflowVariableDefinitions.Combine(new("v", "Value", "boolean"), [V("true")]));
    }

    [Fact]
    public void Variables_round_trip_and_never_become_executable_steps()
    {
        var document = Document();
        var saved = WorkflowDefinitionJson.Deserialize(WorkflowDefinitionJson.Serialize(document))!;
        Assert.True(new WorkflowDefinitionValidator().Validate(saved).IsValid);
        Assert.True(CustomTaskDefinitions.ValidateRuntime(saved).IsValid);
        var plan = WorkflowNodeDefinitions.Normalize(saved);
        Assert.Equal(["a", "b", "consumer"], plan.Steps.Select(step => step.Id));
        Assert.Equal([B("a"), B("b")], Assert.Single(plan.Variables!).Sources);
        Assert.Null(WorkflowDefinitionJson.Deserialize("""{"schema":"formicae.workflow/v1alpha3","startStepId":"a","steps":[{"id":"a","uses":"builtins.plan"}]}""")!.Variables);
    }

    [Fact]
    public void First_and_override_apply_string_size_limits_to_the_selected_result()
    {
        var largeEventSnapshot = V(new string('x', 16001));
        Assert.Equal("small", WorkflowVariableDefinitions.Combine(new("v", "Value", "string", "first"), [V("small"), largeEventSnapshot])!.Value.GetString());
        Assert.Equal("small", WorkflowVariableDefinitions.Combine(new("v", "Value", "string", "override"), [largeEventSnapshot, V("small")])!.Value.GetString());
        Assert.Throws<InvalidOperationException>(() => WorkflowVariableDefinitions.Combine(new("v", "Value", "string", "first"), [largeEventSnapshot, V("small")]));
    }

    [Fact]
    public void Event_sources_through_variables_require_the_guaranteed_selected_entrypoint()
    {
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "",
            [new("created", "github.issue-created", "consumer", Event: V(new { enabled = false, repositoryIds = Array.Empty<Guid>() })), Consumer()],
            Variables: [new("combined", "Issue", "string", Sources: [new("created", "issue")])]);
        Assert.True(CustomTaskDefinitions.ValidateRuntime(document).IsValid);
        var alternate = new WorkflowDefinitionStep("manual", "builtins.start", "consumer", Event: V(new { enabled = true }));
        Assert.False(CustomTaskDefinitions.ValidateRuntime(document with { StartStepId = "manual", Steps = [.. document.Steps, alternate] }).IsValid);
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("duplicate")]
    [InlineData("type")]
    [InlineData("id")]
    [InlineData("missing")]
    [InlineData("port")]
    public void Invalid_variable_graphs_are_rejected(string problem)
    {
        var document = Document(); var variable = document.Variables![0];
        variable = problem switch
        {
            "cycle" => variable with { Sources = [new("combined", "value")] },
            "duplicate" => variable with { Sources = [B("a"), B("a")] },
            "type" => variable with { ValueType = "boolean" },
            "id" => variable with { Id = "a" },
            "port" => variable with { Sources = [new("a", "missing")] },
            _ => variable with { Sources = [B("missing")] }
        };
        Assert.False(CustomTaskDefinitions.ValidateRuntime(document with { Variables = [variable] }).IsValid);
    }

    [Fact]
    public void Chains_validate_every_leaf_even_when_first_would_ignore_it()
    {
        var document = Document();
        document = document with { Variables = [document.Variables![0], new("outer", "Outer", "string", "first", [new("combined", "value")])],
            Steps = [document.Steps[0], document.Steps[1], Consumer(source: "outer")] };
        Assert.True(CustomTaskDefinitions.ValidateRuntime(document).IsValid);
        document = document with { Variables = [document.Variables![0] with { Sources = [B("a"), B("consumer")] }, document.Variables[1]] };
        Assert.False(CustomTaskDefinitions.ValidateRuntime(document).IsValid);
    }

    [Fact]
    public void Loop_exit_and_conditional_producers_remain_invalid_through_variables()
    {
        var document = Document();
        var decision = new WorkflowDefinitionStep("decision", "builtins.decision", Decision: new(new("literal", "boolean", "exists", Value: V(true)), "a", "consumer"));
        Assert.False(CustomTaskDefinitions.ValidateRuntime(document with { StartStepId = "decision", Steps = [decision, document.Steps[0], document.Steps[1], document.Steps[2]] }).IsValid);
        var loop = new WorkflowDefinitionStep("loop", "builtins.loop", "consumer", Loop: new("a", 2, 2));
        Assert.False(CustomTaskDefinitions.ValidateRuntime(document with { StartStepId = "loop", Steps = [loop, document.Steps[0], document.Steps[1] with { NextStepId = "loop", NextStepPort = "return" }, document.Steps[2]] }).IsValid);
        var inside = document with { StartStepId = "loop", Steps = [loop with { NextStepId = "finish" }, .. document.Steps.Take(2), document.Steps[2] with { NextStepId = "loop", NextStepPort = "return" }, new("finish", "builtins.plan")] };
        Assert.True(CustomTaskDefinitions.ValidateRuntime(inside).IsValid);
    }

    [Fact]
    public async Task Prepared_chain_freezes_all_sources_and_detects_tampering()
    {
        var document = Document(); var current = "old";
        document = document with { Variables = [document.Variables![0], new("outer", "Outer", "string", Sources: [new("combined", "value")])] };
        Task<CustomTaskInputProvenance> Load(CustomTaskInputBinding binding) => Task.FromResult(new CustomTaskInputProvenance(binding.StepId, binding.OutputName, Guid.NewGuid(), Guid.NewGuid(), null, V(current + binding.StepId)));
        var evidence = await WorkflowVariableDefinitions.ResolveAsync(document, new("outer", "value"), Load);
        WorkflowVariableDefinitions.ValidatePinnedEvidence([evidence], document, null);
        var settings = Consumer(source: "outer").CustomTask!;
        var prepared = CustomTaskDefinitions.Prepare(settings, new() { IssueUrl = "issue", RepositoryUrl = "repo" }, new Dictionary<string, CustomTaskInputProvenance> { ["result"] = evidence });
        current = "new";
        var restored = JsonSerializer.Deserialize<PreparedCustomTaskExecution>(JsonSerializer.Serialize(prepared))!;
        CustomTaskDefinitions.ValidatePrepared(restored, settings);
        Assert.Equal("Use olda\noldb", restored.Prompt);
        Assert.Throws<InvalidOperationException>(() => WorkflowVariableDefinitions.ValidateEvidence(evidence with { Value = V("tampered") }));
        Assert.Throws<InvalidOperationException>(() => WorkflowVariableDefinitions.ValidatePinnedEvidence([evidence], document with { Variables = [document.Variables![0] with { Mode = "override" }, document.Variables[1]] }, null));
    }

    [Fact]
    public async Task Absent_variable_uses_consumer_default_and_unused_variable_is_valid()
    {
        var document = Document() with { Variables = [new("combined", "Combined", "string")] };
        Assert.True(CustomTaskDefinitions.ValidateRuntime(document).IsValid);
        var evidence = await WorkflowVariableDefinitions.ResolveAsync(document, new("combined", "value"), _ => throw new Exception("No producer should load."));
        var settings = Consumer().CustomTask!;
        settings = settings with { Snapshot = settings.Snapshot! with { Inputs = [new("result", "string", true, V("fallback"))] } };
        Assert.Equal("Use fallback", CustomTaskDefinitions.Prepare(settings, new() { IssueUrl = "issue", RepositoryUrl = "repo" }, new Dictionary<string, CustomTaskInputProvenance> { ["result"] = evidence }).Prompt);
        Assert.True(new WorkflowDefinitionValidator().Validate(document with { Steps = [new("a", "builtins.plan")], Variables = [new("unused", "Unused", "boolean")] }).IsValid);
    }
}
