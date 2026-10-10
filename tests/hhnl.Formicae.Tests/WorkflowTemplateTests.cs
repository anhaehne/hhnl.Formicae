using System.Reflection;
using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowTemplateTests
{
    private static WorkflowDefinitionStep Node(string id, string template, string? next = null,
        IReadOnlyList<CustomTaskInputDefinition>? inputs = null, IReadOnlyDictionary<string, CustomTaskInputBinding>? bindings = null)
        => new(id, WorkflowTemplateDefinitions.Uses, next, Template: new(template, inputs ?? [], bindings));

    [Fact]
    public void Scriban_renders_multiple_scalar_inputs_conditionals_and_functions_without_reprocessing_input()
    {
        var settings = new WorkflowTemplateSettings("{{ inputs.name | string.upcase }}: {{ inputs.count }}\n{{ if inputs.ready }}Ready{{ else }}Waiting{{ end }} {{ inputs.raw }}",
            [new("name", "string", true), new("count", "number", true), new("ready", "boolean", true), new("raw", "string", true)]);
        var values = new Dictionary<string, JsonElement> { ["name"] = JsonSerializer.SerializeToElement("Alice"), ["count"] = JsonSerializer.SerializeToElement(2.5m),
            ["ready"] = JsonSerializer.SerializeToElement(true), ["raw"] = JsonSerializer.SerializeToElement("{{ inputs.name }} <tag> ✓") };
        Assert.Equal("ALICE: 2.5\nReady {{ inputs.name }} <tag> ✓", WorkflowTemplateDefinitions.Render(settings, values));
    }

    [Theory]
    [InlineData("{{ inputs.missing }}")]
    [InlineData("{{ while true }}x{{ end }}")]
    [InlineData("{{ for i in 1..1000 }}12345678901234567{{ end }}")]
    [InlineData("{{ include 'secrets.txt' }}")]
    [InlineData("{{ 'x' | string.repeat 17000 }}")]
    public void Rendering_reports_missing_values_and_resource_limits(string template)
        => Assert.Throws<InvalidOperationException>(() => WorkflowTemplateDefinitions.Render(new(template, []), new Dictionary<string, JsonElement>()));

    [Fact]
    public void Empty_and_literal_templates_are_valid_and_preserve_whitespace()
    {
        Assert.Empty(WorkflowTemplateDefinitions.ValidateStep(Node("template", "")));
        Assert.Equal("", WorkflowTemplateDefinitions.Render(new("", []), new Dictionary<string, JsonElement>()));
        Assert.Equal("  Hello\n世界\n", WorkflowTemplateDefinitions.Render(new("  Hello\n世界\n", []), new Dictionary<string, JsonElement>()));
    }

    [Theory]
    [InlineData("syntax")]
    [InlineData("duplicate")]
    [InlineData("unbound")]
    [InlineData("unknown")]
    [InlineData("name")]
    [InlineData("model")]
    [InlineData("other")]
    public void Validation_rejects_invalid_templates_inputs_and_settings(string invalid)
    {
        var node = Node("template", "{{ inputs.name }}", inputs: [new("name", "string", true)], bindings: new Dictionary<string, CustomTaskInputBinding> { ["name"] = new("producer", "text") });
        node = invalid switch
        {
            "syntax" => node with { Template = node.Template! with { Template = "{{ if }}" } },
            "duplicate" => node with { Template = node.Template! with { Inputs = [new("name", "string", true), new("name", "string", true)] } },
            "unbound" => node with { Template = node.Template! with { Bindings = null } },
            "unknown" => node with { Template = node.Template! with { Bindings = new Dictionary<string, CustomTaskInputBinding> { ["extra"] = new("producer", "text") } } },
            "name" => node with { Template = node.Template! with { Inputs = [new("bad-name", "string", true)] } },
            "model" => node with { Model = "model" },
            _ => node with { Uses = "builtins.plan" }
        };
        Assert.NotEmpty(WorkflowTemplateDefinitions.ValidateStep(node));
    }

    [Fact]
    public void More_than_32_inputs_round_trip_and_validate_without_a_count_cap()
    {
        var inputs = Enumerable.Range(1, 65).Select(i => new CustomTaskInputDefinition($"input{i}", "string", true)).ToArray();
        var bindings = inputs.ToDictionary(input => input.Name, _ => new CustomTaskInputBinding("producer", "text"));
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "producer", [Node("producer", "text", "consumer"), Node("consumer", "{{ inputs.input65 }}", inputs: inputs, bindings: bindings)]);
        var saved = WorkflowDefinitionJson.Deserialize(WorkflowDefinitionJson.Serialize(document))!;
        Assert.True(new WorkflowDefinitionValidator().Validate(saved).IsValid);
        Assert.True(CustomTaskDefinitions.ValidateRuntime(saved).IsValid);
        Assert.Equal(65, saved.Steps[1].Template!.Inputs.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Binding_validation_rejects_type_mismatch_and_downstream_producers(bool mismatch)
    {
        var producer = Node("producer", "value", "consumer");
        var consumer = Node("consumer", "{{ inputs.value }}", inputs: [new("value", mismatch ? "number" : "string", true)],
            bindings: new Dictionary<string, CustomTaskInputBinding> { ["value"] = new("producer", "text") });
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, mismatch ? "producer" : "consumer",
            mismatch ? [producer, consumer] : [producer with { NextStepId = null }, consumer with { NextStepId = "producer" }]);
        Assert.False(CustomTaskDefinitions.ValidateRuntime(document).IsValid);
    }

    [Theory]
    [InlineData("sequential")]
    [InlineData("graph")]
    [InlineData("cycle")]
    public async Task Inline_rendering_routes_durably_and_exposes_consumable_text_without_workers(string mode)
    {
        var steps = new List<WorkflowDefinitionStep> { Node("producer", "Hello **world**\n✓", "consumer"),
            Node("consumer", "Result: {{ inputs.value }}", mode == "cycle" ? "producer" : null, [new("value", "string", true)],
                new Dictionary<string, CustomTaskInputBinding> { ["value"] = new("producer", "text") }) };
        if (mode == "graph") { steps[0] = steps[0] with { NextStepIds = ["other"] }; steps.Add(Node("other", "Other")); }
        var (store, workflow) = await Setup(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "producer", steps));
        for (var i = 0; i < 8 && workflow.Status != WorkflowStatus.Completed && (await store.ListTaskRunsAsync(workflow.Id, default)).All(run => run.DefinitionStepId != "consumer" || run.Status != TaskRunStatus.Succeeded); i++)
            await Orchestrator(store).AdvanceAsync(workflow, default);
        var consumer = (await store.ListTaskRunsAsync(workflow.Id, default)).First(run => run.DefinitionStepId == "consumer");
        Assert.Equal(TaskRunStatus.Succeeded, consumer.Status);
        Assert.Equal("Result: Hello **world**\n✓", consumer.Output);
        Assert.Equal(consumer.Output, consumer.ToResponse().StructuredOutputs!["text"].GetString());
        Assert.Equal("producer", consumer.ToResponse().TemplateExecution!.Provenance["value"].StepId);
        Assert.Null(consumer.ExternalId); Assert.NotNull(consumer.ExecutionAttemptId);
        if (mode != "cycle")
        {
            Assert.Equal(WorkflowStatus.Completed, workflow.Status);
            var count = (await store.ListTaskRunsAsync(workflow.Id, default)).Count;
            await Orchestrator(store).AdvanceAsync(workflow, default);
            Assert.Equal(count, (await store.ListTaskRunsAsync(workflow.Id, default)).Count);
        }
        else Assert.NotEqual(WorkflowStatus.Failed, workflow.Status);
    }

    [Fact]
    public async Task Failed_render_preserves_frozen_inputs_and_attempt_evidence_on_retry()
    {
        var (store, workflow) = await Setup(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "producer", [Node("producer", "Frozen", "consumer"),
            Node("consumer", "{{ inputs.value }} {{ inputs.missing }}", inputs: [new("value", "string", true)], bindings: new Dictionary<string, CustomTaskInputBinding> { ["value"] = new("producer", "text") })]));
        for (var i = 0; i < 4 && workflow.Status != WorkflowStatus.Failed; i++) await Orchestrator(store).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status);
        var consumer = (await store.ListTaskRunsAsync(workflow.Id, default)).Single(run => run.DefinitionStepId == "consumer");
        var evidence = consumer.CustomTaskExecutionJson; var attempt = consumer.ExecutionAttemptId;
        Assert.Null(consumer.StructuredOutputsJson);
        await new WorkflowService(store).RetryWorkflowAsync(workflow.Id, default);
        Assert.Equal(evidence, consumer.CustomTaskExecutionJson); Assert.NotEqual(attempt, consumer.ExecutionAttemptId);
        await Orchestrator(store).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status);
        Assert.Equal(evidence, Assert.Single(await store.ListTaskRunAttemptsAsync(workflow.Id, default)).CustomTaskExecutionJson);
    }

    [Fact]
    public async Task Template_consumes_aggregate_variables_with_frozen_source_evidence()
    {
        var bindings = new Dictionary<string, CustomTaskInputBinding> { ["value"] = new("combined", "value") };
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "first", [
            Node("first", "A", "second"), Node("second", "B", "consumer"),
            Node("consumer", "{{ inputs.value }}", inputs: [new("value", "string", true)], bindings: bindings)],
            Variables: [new("combined", "Combined", "string", Sources: [new("first", "text"), new("second", "text")], Separator: " / ")]);
        var (store, workflow) = await Setup(document);
        for (var i = 0; i < 5 && workflow.Status != WorkflowStatus.Completed; i++) await Orchestrator(store).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        var consumer = (await store.ListTaskRunsAsync(workflow.Id, default)).Single(run => run.DefinitionStepId == "consumer");
        Assert.Equal("A / B", consumer.Output);
        Assert.Equal(2, consumer.ToResponse().TemplateExecution!.Provenance["value"].Variable!.Sources.Count);
    }

    [Fact]
    public async Task Loop_templates_use_current_iteration_outputs()
    {
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "loop", [
            new("loop", WorkflowNodeDefinitions.LoopUses, "after", Loop: new("producer", 2, 2)),
            Node("producer", "Loop value", "consumer"),
            Node("consumer", "{{ inputs.value }}", "loop", [new("value", "string", true)],
                new Dictionary<string, CustomTaskInputBinding> { ["value"] = new("producer", "text") }) with { NextStepPort = "return" },
            Node("after", "Done")]);
        var (store, workflow) = await Setup(document);
        for (var i = 0; i < 8 && workflow.Status != WorkflowStatus.Completed; i++) await Orchestrator(store).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        var consumers = (await store.ListTaskRunsAsync(workflow.Id, default)).Where(run => run.DefinitionStepId == "consumer").ToArray();
        Assert.Equal(2, consumers.Length);
        Assert.All(consumers, run => { Assert.Equal("Loop value", run.Output); Assert.Equal(run.LoopIteration, run.ToResponse().TemplateExecution!.Provenance["value"].LoopIteration); });
    }

    private static async Task<(InMemoryWorkflowStore Store, Workflow Workflow)> Setup(WorkflowDefinitionDocument document)
    {
        Assert.True(new WorkflowDefinitionValidator().Validate(document).IsValid); Assert.True(CustomTaskDefinitions.ValidateRuntime(document).IsValid);
        var store = new InMemoryWorkflowStore(); var definition = await store.CreateWorkflowDefinitionAsync(new() { Name = "Templates" }, default);
        var version = await store.CreateWorkflowDefinitionVersionAsync(new() { WorkflowDefinitionId = definition.Id, Version = 1, DslSchemaVersion = document.Schema,
            DefinitionJson = WorkflowDefinitionJson.Serialize(document), IsEnabled = true }, default);
        var started = await new WorkflowService(store, workflowDefinitions: new WorkflowDefinitionService(store, new())).StartGitHubIssueWorkflowAsync(new(
            "https://github.com/acme/repo/issues/1", "https://github.com/acme/repo", "main", null, definition.Id, version.Id), default);
        return (store, (await store.GetWorkflowAsync(started.WorkflowId, default))!);
    }
    private static WorkflowOrchestrator Orchestrator(IWorkflowStore store) => new(store, DispatchProxy.Create<IWorkItemProvider, Forbidden>(),
        DispatchProxy.Create<ISourceControlProvider, Forbidden>(), DispatchProxy.Create<IAgentRunner, Forbidden>(), DispatchProxy.Create<IPromptRenderer, Forbidden>());
    public class Forbidden : DispatchProxy
    { protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new InvalidOperationException("Unexpected worker/provider call: " + method!.Name); }
}
