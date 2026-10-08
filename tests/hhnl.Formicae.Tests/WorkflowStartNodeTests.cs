using System.Text.Json;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowStartNodeTests
{
    private static WorkflowDefinitionDocument Document(bool manual = true) => new(DefaultWorkflowDefinitions.V1Alpha3Schema, manual ? "manual" : "", [
        ..manual ? new WorkflowDefinitionStep[] { new("manual", WorkflowStartDefinitions.Uses, "plan", Trigger: new(WorkflowTriggerType.Manual, true, [], null)) } : [],
        ..manual ? new WorkflowDefinitionStep[] { new("plan", "builtins.plan") } : [],
        new("hook", WorkflowStartDefinitions.Uses, "event-plan", Trigger: new(WorkflowTriggerType.Webhook, true, [], null, WebhookSecretName: "fixture")),
        new("event-plan", "builtins.plan")
    ]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Start_nodes_round_trip_and_compile_without_worker_tasks(bool manual)
    {
        var document = WorkflowDefinitionJson.Deserialize(WorkflowDefinitionJson.Serialize(Document(manual)))!;
        Assert.True(new WorkflowDefinitionValidator().Validate(document).IsValid);
        var plan = WorkflowNodeDefinitions.Normalize(document);
        Assert.Equal(manual ? "plan" : "event-plan", plan.StartStepId);
        Assert.Equal("hook", Assert.Single(plan.Triggers!).Id);
        Assert.Equal("event-plan", plan.Triggers![0].NextStepId);
        Assert.DoesNotContain(plan.Steps, step => step.Uses == WorkflowStartDefinitions.Uses);
        Assert.Equal("fixture", document.Steps.Single(step => step.Id == "hook").Trigger!.WebhookSecretName);
    }

    [Theory]
    [InlineData("multiple-manual")]
    [InlineData("wrong-manual")]
    [InlineData("incoming")]
    [InlineData("missing-target")]
    [InlineData("multiple-targets")]
    [InlineData("start-target")]
    [InlineData("worker-settings")]
    [InlineData("missing-secret")]
    [InlineData("unsafe-secret")]
    public void Invalid_start_contracts_are_rejected(string problem)
    {
        var document = Document(); var steps = document.Steps.ToList();
        switch (problem)
        {
            case "multiple-manual": steps.Add(steps[0] with { Id = "second" }); break;
            case "wrong-manual": document = document with { StartStepId = "plan" }; break;
            case "incoming": steps[1] = steps[1] with { NextStepId = "manual" }; break;
            case "missing-target": steps[0] = steps[0] with { NextStepId = "missing" }; break;
            case "multiple-targets": steps[0] = steps[0] with { NextStepIds = ["event-plan"] }; break;
            case "start-target": steps[0] = steps[0] with { NextStepId = "hook" }; break;
            case "worker-settings": steps[0] = steps[0] with { AiSettingsId = "ai" }; break;
            case "missing-secret": steps[2] = steps[2] with { Trigger = steps[2].Trigger! with { WebhookSecretName = null } }; break;
            case "unsafe-secret": steps[2] = steps[2] with { Trigger = steps[2].Trigger! with { WebhookSecretName = "nested:secret" } }; break;
        }
        Assert.Contains(new WorkflowDefinitionValidator().Validate(document with { Steps = steps }).Errors, error => error.Code == "definition.start.invalid");
    }

    [Fact]
    public async Task Manual_and_webhook_entries_execute_independently_and_record_selected_start()
    {
        var store = new InMemoryWorkflowStore(); var definitions = new WorkflowDefinitionService(store, new());
        var definition = await definitions.CreateAsync(new("Starts"), default);
        var version = await definitions.CreateVersionAsync(definition.Id, new(null, true, false, Document()), default);
        var workflows = new WorkflowService(store, workflowDefinitions: definitions);
        foreach (var start in new[] { "manual", "hook" })
        {
            var summary = await workflows.StartGitHubIssueWorkflowAsync(new($"https://example.com/issues/{start}", "https://example.com/repo", null, null, definition.Id, version.Id), default, start == "manual" ? null : start);
            var workflow = (await store.GetWorkflowAsync(summary.WorkflowId, default))!;
            var selected = JsonDocument.Parse(Assert.Single(await store.ListEventsAsync(workflow.Id, default)).DetailsJson!);
            Assert.Equal(start, selected.RootElement.GetProperty("startNodeId").GetString());
            var orchestrator = new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), new FakeAgentRunner(), new Prompt());
            await orchestrator.AdvanceAsync(workflow, default);
            Assert.Equal(WorkflowStatus.Completed, workflow.Status);
            Assert.Equal(start == "manual" ? "plan" : "event-plan", Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default)).DefinitionStepId);
        }
        Assert.Equal(WorkflowDefinitionJson.Serialize(version.Definition), (await store.GetWorkflowDefinitionVersionAsync(version.Id, default))!.DefinitionJson);
    }

    [Fact]
    public async Task Trigger_only_and_disabled_manual_definitions_reject_manual_runs_without_creating_work()
    {
        foreach (var document in new[] { Document(false), Document() with { Steps = Document().Steps.Select(step => step.Id == "manual" ? step with { Trigger = step.Trigger! with { Enabled = false } } : step).ToArray() } })
        {
            var store = new InMemoryWorkflowStore(); var definitions = new WorkflowDefinitionService(store, new());
            var definition = await definitions.CreateAsync(new("Triggered"), default);
            var version = await definitions.CreateVersionAsync(definition.Id, new(null, true, false, document), default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowService(store, workflowDefinitions: definitions).StartGitHubIssueWorkflowAsync(new("https://example.com/issues/1", "https://example.com/repo", null, null, definition.Id, version.Id), default));
            Assert.Empty(await store.ListRecentWorkflowsAsync(10, default));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Starts_can_enter_existing_loops_and_decisions(bool decision)
    {
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "manual", [
            new("manual", WorkflowStartDefinitions.Uses, "control", Trigger: new(WorkflowTriggerType.Manual, true, [], null)),
            decision ? new("control", WorkflowDecisionDefinitions.Uses, Decision: new(new("literal", "boolean", "equals", Value: JsonSerializer.SerializeToElement(true), CompareTo: JsonSerializer.SerializeToElement(true)), "body", "finish"))
                : new("control", WorkflowNodeDefinitions.LoopUses, "finish", Loop: new("body", 1, 1)),
            new("body", "builtins.plan", decision ? "finish" : "control", NextStepPort: decision ? null : "return"),
            new("finish", "builtins.plan")
        ]);
        Assert.True(new WorkflowDefinitionValidator().Validate(document).IsValid);
        Assert.Equal(decision ? "control" : "body", WorkflowNodeDefinitions.Normalize(document).StartStepId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Alternative_graph_starts_share_a_join_without_waiting_for_inactive_paths(bool triggered)
    {
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "manual", [
            new("manual", WorkflowStartDefinitions.Uses, "a", Trigger: new(WorkflowTriggerType.Manual, true, [], null)),
            new("hook", WorkflowStartDefinitions.Uses, "b", Trigger: new(WorkflowTriggerType.Webhook, true, [], null, WebhookSecretName: "fixture")),
            new("a", "builtins.plan", "join", NextStepIds: ["sibling"]),
            new("b", "builtins.plan", "join"),
            new("sibling", "builtins.plan", "join"), new("join", "builtins.plan")
        ]);
        Assert.True(new WorkflowDefinitionValidator().Validate(document).IsValid);
        var store = new InMemoryWorkflowStore(); var definitions = new WorkflowDefinitionService(store, new());
        var definition = await definitions.CreateAsync(new("Converging starts"), default);
        var version = await definitions.CreateVersionAsync(definition.Id, new(null, true, false, document), default);
        var summary = await new WorkflowService(store, workflowDefinitions: definitions).StartGitHubIssueWorkflowAsync(new("https://example.com/issues/1", "https://example.com/repo", null, null, definition.Id, version.Id), default, triggered ? "hook" : null);
        var workflow = (await store.GetWorkflowAsync(summary.WorkflowId, default))!;
        for (var tick = 0; tick < 5; tick++) await new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), new FakeAgentRunner(), new Prompt()).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        var runs = await store.ListTaskRunsAsync(workflow.Id, default);
        Assert.Equal(triggered ? 2 : 3, runs.Count);
        Assert.DoesNotContain(runs, run => run.DefinitionStepId == (triggered ? "a" : "b"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Graph_bindings_require_the_producer_from_every_start_reaching_the_consumer(bool sharedProducer)
    {
        var catalog = new CustomTaskService(new InMemoryCustomTaskStore());
        var consumer = await catalog.CreateAsync(new("Consumer", "Use {{input.text}}", Inputs: [new("text", "string", true)]), default);
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "manual", [
            new("manual", WorkflowStartDefinitions.Uses, "producer", Trigger: new(WorkflowTriggerType.Manual, true, [], null)),
            new("hook", WorkflowStartDefinitions.Uses, sharedProducer ? "producer" : "other", Trigger: new(WorkflowTriggerType.Webhook, true, [], null, WebhookSecretName: "fixture")),
            new("producer", "builtins.script", "consumer", Script: new("echo output"), NextStepIds: ["other"]),
            new("other", "builtins.plan", "consumer"),
            new("consumer", CustomTaskDefinitions.Uses, CustomTask: new(consumer.Id, Bindings: new Dictionary<string, CustomTaskInputBinding> { ["text"] = new("producer", "output") }))
        ]);
        var definitions = new WorkflowDefinitionService(new InMemoryWorkflowStore(), new(), customTasks: catalog);
        var validation = await definitions.ValidateAsync(document, default);
        Assert.Equal(sharedProducer, validation.IsValid);
        if (!sharedProducer) Assert.Contains(validation.Errors, error => error.NodeId == "consumer" && error.Message.Contains("guaranteed"));
    }

    [Fact]
    public async Task Provider_start_nodes_preserve_matching_overrides_and_delivery_audit()
    {
        var store = new InMemoryWorkflowStore(); var integrations = new InMemoryDevOpsIntegrationStore();
        var integration = await integrations.CreateAsync(new DevOpsIntegration { ProviderType = DevOpsProviderType.GitHub, DisplayName = "GitHub" }, default);
        var repository = await integrations.AddRepositoryAsync(new ConnectedRepository { DevOpsIntegrationId = integration.Id,
            Owner = "acme", Name = "repo", RepositoryUrl = "https://example.test/repo", DefaultBranch = "main" }, default);
        var definitions = new WorkflowDefinitionService(store, new(), integrations);
        var definition = await definitions.CreateAsync(new("Provider start"), default);
        var document = Document(false) with { Steps = [
            new("github", WorkflowStartDefinitions.Uses, "event-plan", Trigger: new(WorkflowTriggerType.DevOpsIssueLabel, true, [repository.Id], "ready", "develop", "provider-model")),
            new("event-plan", "builtins.plan")] };
        var version = await definitions.CreateVersionAsync(definition.Id, new(null, true, false, document), default);
        var workflows = new WorkflowService(store, workflowDefinitions: definitions);
        var triggers = new WorkflowTriggerService(store, integrations, workflows);
        var delivery = new DevOpsIssueLabelTriggerEvent(DevOpsProviderType.GitHub, "provider-delivery", "issues", "labeled", repository.RepositoryUrl,
            "https://example.test/issues/1", "ready", "acme/repo");
        var id = Assert.Single(await triggers.HandleIssueLabelEventAsync(delivery, default));
        var workflow = (await store.GetWorkflowAsync(id, default))!;
        Assert.Equal("event-plan", workflow.CurrentDefinitionStepId); Assert.Equal(version.Id, workflow.WorkflowDefinitionVersionId);
        Assert.Equal("develop", workflow.BaseBranch); Assert.Equal("provider-model", workflow.Model);
        Assert.Equal("github", Assert.Single(await store.ListTriggerEventsAsync(id, default)).TriggerId);
        Assert.Contains("github", Assert.Single(await store.ListEventsAsync(id, default)).DetailsJson!);
        Assert.Empty(await triggers.HandleIssueLabelEventAsync(delivery with { IssueUrl = "https://example.test/issues/2" }, default));
    }

    private sealed class Prompt : IPromptRenderer
    {
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? item, CancellationToken token)
            => Task.FromResult("plan");
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? item, IReadOnlyList<PullRequestComment> comments, CancellationToken token)
            => Task.FromResult("plan");
    }
}
