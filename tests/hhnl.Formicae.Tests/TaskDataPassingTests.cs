using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Infrastructure.OpenHands;

namespace hhnl.Formicae.Tests;

public sealed class TaskDataPassingTests
{
    private static JsonElement Value<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static CustomTaskSnapshot Producer(string type = "string", bool required = true) => new("producer", 1, "Producer", "", "Produce", [], new(), [new("summary", type, required)]);
    private static CustomTaskSnapshot Consumer(string type = "string") => new("consumer", 1, "Consumer", "", "Consume {{input.summary}}", [new("summary", type, true)], new());
    private static WorkflowCustomTaskSettings Binding(CustomTaskSnapshot? consumer = null, string step = "producer", string output = "summary")
        => new("consumer", Snapshot: consumer ?? Consumer(), Bindings: new Dictionary<string, CustomTaskInputBinding> { ["summary"] = new(step, output) });
    private static WorkflowDefinitionStep P(string? next = "consumer") => new("producer", CustomTaskDefinitions.Uses, next, CustomTask: new("producer", Snapshot: Producer()));
    private static WorkflowDefinitionStep C(string? next = null) => new("consumer", CustomTaskDefinitions.Uses, next, CustomTask: Binding());
    private static WorkflowDefinitionDocument Document(params WorkflowDefinitionStep[] steps) => new(DefaultWorkflowDefinitions.V1Alpha3Schema, steps[0].Id, steps);

    [Fact]
    public async Task Catalog_outputs_are_immutable_and_old_records_default_to_empty()
    {
        var store = new InMemoryCustomTaskStore(); var service = new CustomTaskService(store);
        var outputs = new List<CustomTaskOutputDefinition> { new("summary", "string", true) };
        var created = await service.CreateAsync(new("Producer", "Produce", Outputs: outputs), default); outputs.Clear();
        Assert.Equal(new("summary", "string", true), Assert.Single((await service.GetAsync(created.Id, default))!.Outputs));
        var updated = (await service.UpdateAsync(created.Id, new(1, "Producer", "Produce", Outputs: [new("ready", "boolean")]), default))!;
        Assert.Equal("ready", Assert.Single(updated.Outputs).Name);
        Assert.Equal("summary", Assert.Single(created.Outputs).Name);
        Assert.Empty((await service.CreateAsync(new("Legacy", "Prompt"), default)).Outputs);
        var old = JsonSerializer.Deserialize<CustomTaskSnapshot>("""{"id":"old","revision":1,"name":"Old","description":"","promptTemplate":"Prompt","inputs":[],"runner":{"kind":"agent","timeoutSeconds":30}}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Empty(old.Outputs);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("limit")]
    [InlineData("null")]
    public async Task Invalid_output_schemas_are_rejected(string invalid)
    {
        IReadOnlyList<CustomTaskOutputDefinition> outputs = invalid switch
        {
            "duplicate" => [new("a", "string"), new("a", "number")], "name" => [new("bad-name", "string")],
            "type" => [new("a", "object")], "null" => [null!], _ => Enumerable.Range(0, 33).Select(i => new CustomTaskOutputDefinition($"a{i}", "string")).ToArray()
        };
        await Assert.ThrowsAsync<ArgumentException>(() => new CustomTaskService(new InMemoryCustomTaskStore()).CreateAsync(new("Task", "Prompt", Outputs: outputs), default));
    }

    [Theory]
    [InlineData("{\"summary\":\"ready\"}", "string")]
    [InlineData("{\"summary\":42}", "number")]
    [InlineData("{\"summary\":false}", "boolean")]
    public void Strict_outputs_accept_supported_scalars(string response, string type)
        => Assert.Single(CustomTaskDefinitions.ParseOutputs(response, Producer(type).Outputs));

    [Theory]
    [InlineData("not json")]
    [InlineData("```json\n{\"summary\":\"ready\"}\n```")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"summary\":null}")]
    [InlineData("{\"summary\":42}")]
    [InlineData("{\"summary\":\"a\",\"summary\":\"b\"}")]
    [InlineData("{\"summary\":\"a\",\"unknown\":true}")]
    [InlineData("{\"summary\":\"a\",}")]
    public void Strict_outputs_reject_malformed_or_schema_violating_responses(string response)
        => Assert.Throws<InvalidOperationException>(() => CustomTaskDefinitions.ParseOutputs(response, Producer().Outputs));

    [Fact]
    public void Output_limits_and_unsafe_numbers_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => CustomTaskDefinitions.ParseOutputs(JsonSerializer.Serialize(new { summary = new string('x', 16001) }), Producer().Outputs));
        Assert.Throws<InvalidOperationException>(() => CustomTaskDefinitions.ParseOutputs(JsonSerializer.Serialize(new { summary = new string('é', 15000) }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), Producer().Outputs));
        Assert.Throws<InvalidOperationException>(() => CustomTaskDefinitions.ParseOutputs(new string('x', 65537), Producer().Outputs));
        Assert.Throws<InvalidOperationException>(() => CustomTaskDefinitions.ParseOutputs("{\"summary\":9007199254740993}", Producer("number").Outputs));
    }

    [Fact]
    public void Bindings_require_matching_declared_parameters_and_no_literal()
    {
        Assert.True(CustomTaskDefinitions.ValidateRuntime(Document(P(), C())).IsValid);
        var conflict = C() with { CustomTask = Binding() with { Inputs = new Dictionary<string, JsonElement> { ["summary"] = Value("literal") } } };
        Assert.Contains(CustomTaskDefinitions.ValidateRuntime(Document(P(), conflict)).Errors, error => error.Message.Contains("both"));
        foreach (var settings in new[] { Binding(Consumer("boolean")), Binding(output: "missing"), Binding(step: "missing"), Binding() with { Bindings = new Dictionary<string, CustomTaskInputBinding> { ["undeclared"] = new("producer", "summary") } } })
            Assert.False(CustomTaskDefinitions.ValidateRuntime(Document(P(), C() with { CustomTask = settings })).IsValid);
    }

    [Fact]
    public void Self_downstream_and_conditional_producers_are_rejected_but_dominating_sources_are_allowed()
    {
        Assert.False(CustomTaskDefinitions.ValidateRuntime(Document(P(), C() with { CustomTask = Binding(step: "consumer") })).IsValid);
        Assert.False(CustomTaskDefinitions.ValidateRuntime(Document(C("producer"), P(null))).IsValid);
        var decision = new WorkflowDefinitionStep("decision", "builtins.decision", Decision: new(new("literal", "boolean", "exists", Value: Value(true)), "producer", "consumer"));
        Assert.False(CustomTaskDefinitions.ValidateRuntime(Document(decision, P(), C())).IsValid);
        var afterProducer = decision with { Decision = decision.Decision! with { TrueStepId = "consumer", FalseStepId = "consumer" } };
        Assert.True(CustomTaskDefinitions.ValidateRuntime(Document(P("decision"), afterProducer, C())).IsValid);
    }

    [Fact]
    public void Loop_bindings_stay_in_the_same_body_or_enter_from_before_the_loop()
    {
        var loop = new WorkflowDefinitionStep("loop", "builtins.loop", "finish", Loop: new("producer", 2, 2));
        var finish = new WorkflowDefinitionStep("finish", "builtins.plan");
        Assert.True(CustomTaskDefinitions.ValidateRuntime(Document(loop, P(), C("loop") with { NextStepPort = "return" }, finish)).IsValid);
        Assert.False(CustomTaskDefinitions.ValidateRuntime(Document(loop, P("loop") with { NextStepPort = "return" }, C())).IsValid); // invalid loop exit does not create data eligibility
        var leave = Document(loop, P("loop") with { NextStepPort = "return" }, C() with { Id = "finish" });
        Assert.False(CustomTaskDefinitions.ValidateRuntime(leave).IsValid);
        var enter = Document(P("loop"), loop with { Loop = new("consumer", 2, 2) }, C("loop") with { NextStepPort = "return" }, finish);
        Assert.True(CustomTaskDefinitions.ValidateRuntime(enter).IsValid);
        var secondLoop = new WorkflowDefinitionStep("loop2", "builtins.loop", "finish", Loop: new("consumer", 2, 2));
        Assert.False(CustomTaskDefinitions.ValidateRuntime(Document(loop with { NextStepId = "loop2" }, P("loop") with { NextStepPort = "return" }, secondLoop, C("loop2") with { NextStepPort = "return" }, finish)).IsValid);
    }

    [Fact]
    public void Preparation_freezes_values_defaults_and_identity_and_rejects_tampering()
    {
        var settings = Binding(); var workflow = new Workflow { IssueUrl = "issue", RepositoryUrl = "repo" };
        var provenance = new Dictionary<string, CustomTaskInputProvenance> { ["summary"] = new("producer", "summary", Guid.NewGuid(), Guid.NewGuid(), null, Value("ready")) };
        var prepared = CustomTaskDefinitions.Prepare(settings, workflow, provenance);
        Assert.Equal("Consume ready", prepared.Prompt); CustomTaskDefinitions.ValidatePrepared(prepared, settings);
        Assert.Throws<InvalidOperationException>(() => CustomTaskDefinitions.Prepare(settings, workflow));
        Assert.Throws<InvalidOperationException>(() => CustomTaskDefinitions.ValidatePrepared(prepared with { Inputs = new Dictionary<string, JsonElement> { ["summary"] = Value("changed") } }, settings));
        Assert.Throws<InvalidOperationException>(() => CustomTaskDefinitions.ValidatePrepared(prepared with { Provenance = new Dictionary<string, CustomTaskInputProvenance> { ["summary"] = provenance["summary"] with { StepId = "other" } } }, settings));
        var optional = Binding(Consumer() with { Inputs = [new("summary", "string", true, Value("default"))] });
        provenance["summary"] = provenance["summary"] with { Value = null };
        Assert.Equal("Consume default", CustomTaskDefinitions.Prepare(optional, workflow, provenance).Prompt);
        Assert.Throws<InvalidOperationException>(() => CustomTaskDefinitions.Prepare(settings, workflow, provenance));
        var absent = Binding(Consumer() with { Inputs = [new("summary", "string")] });
        Assert.Empty(CustomTaskDefinitions.Prepare(absent, workflow, provenance).Inputs);
    }

    [Fact]
    public void Cli_final_responses_are_extracted_without_treating_logs_or_tool_events_as_values()
    {
        var codex = JsonSerializer.Serialize(new { type = "item.completed", item = new { type = "agent_message", text = "{\"summary\":\"ready\"}" } });
        var openHands = JsonSerializer.Serialize(new { action = "finish", source = "agent", args = new { final_thought = "{\"summary\":\"ready\"}" } });
        var oldOpenHands = JsonSerializer.Serialize(new { action = "finish", source = "agent", args = new { outputs = new { content = "{\"summary\":\"ready\"}" } } });
        var sdkMessage = JsonSerializer.Serialize(new { kind = "MessageEvent", source = "agent", llm_message = new { role = "assistant", content = new[] { new { type = "text", text = "{\"summary\":\"ready\"}" } } } });
        var sdkFinish = JsonSerializer.Serialize(new { kind = "ActionEvent", source = "agent", action = new { kind = "FinishAction", message = "{\"summary\":\"ready\"}" } });
        foreach (var final in new[] { codex, openHands, oldOpenHands, sdkMessage, sdkFinish }) Assert.Equal("{\"summary\":\"ready\"}", OpenHandsAgentRunner.ExtractFinalResponse("worker log\n{\"summary\":\"fake\"}\n" + final + "\nworker finished"));
        Assert.Null(OpenHandsAgentRunner.ExtractFinalResponse("{\"type\":\"item.completed\",\"item\":\"malformed\"}\n{\"summary\":\"fake\"}\n{\"action\":\"message\",\"source\":\"user\",\"args\":{\"content\":\"fake\"}}\n{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\",\"text\":\"fake\"}}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    public void Empty_cli_final_responses_never_fall_back_to_earlier_valid_outputs(string response)
    {
        var earlier = JsonSerializer.Serialize(new { kind = "MessageEvent", source = "agent", llm_message = new { role = "assistant", content = new[] { new { type = "text", text = "{\"summary\":\"earlier\"}" } } } });
        var finals = new[]
        {
            JsonSerializer.Serialize(new { kind = "ActionEvent", source = "agent", action = new { kind = "FinishAction", message = response } }),
            JsonSerializer.Serialize(new { action = "finish", source = "agent", args = new { final_thought = response } }),
            JsonSerializer.Serialize(new { action = "finish", source = "agent", args = new { outputs = new { content = response } } }),
            JsonSerializer.Serialize(new { kind = "MessageEvent", source = "agent", llm_message = new { role = "assistant", content = new[] { new { type = "text", text = response } } } }),
            JsonSerializer.Serialize(new { type = "item.completed", item = new { type = "agent_message", text = response } })
        };
        foreach (var final in finals)
        {
            var extracted = OpenHandsAgentRunner.ExtractFinalResponse(earlier + "\n" + final);
            Assert.Equal(response, extracted);
            Assert.Throws<InvalidOperationException>(() => CustomTaskDefinitions.ParseOutputs(extracted!, Producer().Outputs));
        }
    }

    [Theory]
    [InlineData("{\"kind\":\"ActionEvent\",\"source\":\"agent\",\"action\":{\"kind\":\"FinishAction\"}}")]
    [InlineData("{\"action\":\"finish\",\"source\":\"agent\",\"args\":{}}")]
    public void Terminal_events_with_missing_responses_do_not_accept_surrounding_messages(string terminal)
    {
        var message = JsonSerializer.Serialize(new { kind = "MessageEvent", source = "agent", llm_message = new { role = "assistant", content = new[] { new { type = "text", text = "{\"summary\":\"message\"}" } } } });
        Assert.Null(OpenHandsAgentRunner.ExtractFinalResponse(message + "\n" + terminal + "\n" + message));
    }
}
