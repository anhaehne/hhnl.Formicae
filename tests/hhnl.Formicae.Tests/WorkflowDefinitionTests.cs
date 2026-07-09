using System.Text.Json;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowDefinitionTests
{
    private readonly WorkflowDefinitionValidator validator = new();

    [Fact]
    public void Validator_accepts_default_mvp_graph()
    {
        var result = validator.Validate(DefaultWorkflowDefinitions.CreateMvpDocument());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validator_rejects_duplicate_step_ids()
    {
        var result = validator.Validate(new WorkflowDefinitionDocument(
            DefaultWorkflowDefinitions.V1Alpha1Schema,
            "plan",
            [
                new WorkflowDefinitionStep("plan", "builtins.plan", "implement"),
                new WorkflowDefinitionStep("plan", "builtins.implement")
            ]));

        Assert.Contains(result.Errors, error => error.Code == "definition.step.id.duplicate");
    }

    [Fact]
    public void Validator_rejects_missing_start_step()
    {
        var result = validator.Validate(new WorkflowDefinitionDocument(
            DefaultWorkflowDefinitions.V1Alpha1Schema,
            "missing",
            [new WorkflowDefinitionStep("plan", "builtins.plan")]));

        Assert.Contains(result.Errors, error => error.Code == "definition.startStepId.invalid");
    }

    [Fact]
    public void Validator_rejects_unknown_next_step()
    {
        var result = validator.Validate(new WorkflowDefinitionDocument(
            DefaultWorkflowDefinitions.V1Alpha1Schema,
            "plan",
            [new WorkflowDefinitionStep("plan", "builtins.plan", "missing")]));

        Assert.Contains(result.Errors, error => error.Code == "definition.step.nextStepId.unknown");
    }

    [Fact]
    public void Validator_rejects_cycles()
    {
        var result = validator.Validate(new WorkflowDefinitionDocument(
            DefaultWorkflowDefinitions.V1Alpha1Schema,
            "plan",
            [
                new WorkflowDefinitionStep("plan", "builtins.plan", "implement"),
                new WorkflowDefinitionStep("implement", "builtins.implement", "plan")
            ]));

        Assert.Contains(result.Errors, error => error.Code == "definition.graph.cycle");
    }

    [Fact]
    public void Validator_rejects_disconnected_steps()
    {
        var result = validator.Validate(new WorkflowDefinitionDocument(
            DefaultWorkflowDefinitions.V1Alpha1Schema,
            "plan",
            [
                new WorkflowDefinitionStep("plan", "builtins.plan"),
                new WorkflowDefinitionStep("implement", "builtins.implement")
            ]));

        Assert.Contains(result.Errors, error => error.Code == "definition.graph.disconnected");
    }

    [Fact]
    public void Validator_rejects_unsupported_uses()
    {
        var result = validator.Validate(new WorkflowDefinitionDocument(
            DefaultWorkflowDefinitions.V1Alpha1Schema,
            "plan",
            [new WorkflowDefinitionStep("plan", "custom.plan")]));

        Assert.Contains(result.Errors, error => error.Code == "definition.step.uses.unsupported");
    }

    [Fact]
    public void Old_definition_without_triggers_deserializes_and_validates()
    {
        const string json = """
            {
              "schema": "formicae.workflow/v1alpha1",
              "startStepId": "plan",
              "steps": [
                { "id": "plan", "uses": "builtins.plan" }
              ]
            }
            """;

        var document = WorkflowDefinitionJson.Deserialize(json);
        var result = validator.Validate(document);

        Assert.NotNull(document);
        Assert.Null(document.Triggers);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validator_accepts_valid_devops_issue_label_trigger()
    {
        var result = validator.Validate(DocumentWithTrigger(new WorkflowDefinitionTrigger(
            "triage",
            WorkflowTriggerType.DevOpsIssueLabel,
            true,
            [Guid.NewGuid()],
            "formicae")));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validator_rejects_enabled_devops_issue_label_without_label()
    {
        var result = validator.Validate(DocumentWithTrigger(new WorkflowDefinitionTrigger(
            "triage",
            WorkflowTriggerType.DevOpsIssueLabel,
            true,
            [Guid.NewGuid()],
            "")));

        Assert.Contains(result.Errors, error => error.Code == "definition.trigger.label.required");
    }

    [Fact]
    public void Validator_rejects_enabled_devops_issue_label_without_repositories()
    {
        var result = validator.Validate(DocumentWithTrigger(new WorkflowDefinitionTrigger(
            "triage",
            WorkflowTriggerType.DevOpsIssueLabel,
            true,
            [],
            "formicae")));

        Assert.Contains(result.Errors, error => error.Code == "definition.trigger.repositories.required");
    }

    [Fact]
    public async Task Saving_enabled_definition_rejects_unknown_trigger_repository()
    {
        var store = new InMemoryWorkflowStore();
        var integrations = new InMemoryDevOpsIntegrationStore();
        var definitionService = new WorkflowDefinitionService(store, validator, integrations);
        var definition = await definitionService.CreateAsync(new CreateWorkflowDefinitionRequest("Triggered workflow"), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() => definitionService.CreateVersionAsync(
            definition.Id,
            new CreateWorkflowDefinitionVersionRequest(null, true, false, DocumentWithTrigger(new WorkflowDefinitionTrigger(
                "triage",
                WorkflowTriggerType.DevOpsIssueLabel,
                true,
                [Guid.NewGuid()],
                "formicae"))),
            CancellationToken.None));

        Assert.Contains(exception.Errors, error => error.Code == "definition.trigger.repository.unknown");
    }

    [Fact]
    public async Task Starting_workflow_without_definition_fields_uses_default_mvp_version()
    {
        var store = new InMemoryWorkflowStore();
        var definitionService = new WorkflowDefinitionService(store, validator);
        var service = new WorkflowService(store, workflowDefinitions: definitionService);

        var started = await service.StartGitHubIssueWorkflowAsync(new StartGitHubIssueWorkflowRequest(
            "https://github.com/acme/widgets/issues/101",
            "https://github.com/acme/widgets",
            null,
            null), CancellationToken.None);

        var workflow = await store.GetWorkflowAsync(started.WorkflowId, CancellationToken.None);
        Assert.NotNull(workflow);
        Assert.Equal(DefaultWorkflowDefinitions.MvpDefinitionId, workflow.WorkflowDefinitionId);
        Assert.Equal(DefaultWorkflowDefinitions.MvpVersionId, workflow.WorkflowDefinitionVersionId);
        Assert.Equal(DefaultWorkflowDefinitions.V1Alpha1Schema, workflow.DslSchemaVersion);
    }

    [Fact]
    public async Task Starting_workflow_with_custom_definition_stores_selected_version()
    {
        var store = new InMemoryWorkflowStore();
        var definitionService = new WorkflowDefinitionService(store, validator);
        var service = new WorkflowService(store, workflowDefinitions: definitionService);
        var definition = await definitionService.CreateAsync(new CreateWorkflowDefinitionRequest("Custom sequential workflow"), CancellationToken.None);
        var version = await definitionService.CreateVersionAsync(
            definition.Id,
            new CreateWorkflowDefinitionVersionRequest(null, true, false, new WorkflowDefinitionDocument(
                DefaultWorkflowDefinitions.V1Alpha1Schema,
                "a",
                [
                    new WorkflowDefinitionStep("a", "builtins.plan", "b"),
                    new WorkflowDefinitionStep("b", "builtins.implement", "c"),
                    new WorkflowDefinitionStep("c", "builtins.create-pull-request", "d"),
                    new WorkflowDefinitionStep("d", "builtins.address-comments")
                ])),
            CancellationToken.None);

        var started = await service.StartGitHubIssueWorkflowAsync(new StartGitHubIssueWorkflowRequest(
            "https://github.com/acme/widgets/issues/102",
            "https://github.com/acme/widgets",
            null,
            null,
            definition.Id,
            version.Id), CancellationToken.None);

        var workflow = await store.GetWorkflowAsync(started.WorkflowId, CancellationToken.None);
        Assert.NotNull(workflow);
        Assert.Equal(definition.Id, workflow.WorkflowDefinitionId);
        Assert.Equal(version.Id, workflow.WorkflowDefinitionVersionId);
        Assert.Equal(DefaultWorkflowDefinitions.V1Alpha1Schema, workflow.DslSchemaVersion);
    }

    [Fact]
    public async Task Starting_workflow_with_missing_version_returns_service_level_not_found()
    {
        var store = new InMemoryWorkflowStore();
        var definitionService = new WorkflowDefinitionService(store, validator);
        var service = new WorkflowService(store, workflowDefinitions: definitionService);

        await Assert.ThrowsAsync<WorkflowDefinitionNotFoundException>(() => service.StartGitHubIssueWorkflowAsync(
            new StartGitHubIssueWorkflowRequest(
                "https://github.com/acme/widgets/issues/103",
                "https://github.com/acme/widgets",
                null,
                null,
                null,
                Guid.Parse("33333333-3333-3333-3333-333333333333")),
            CancellationToken.None));
    }

    private static WorkflowDefinitionDocument DocumentWithTrigger(WorkflowDefinitionTrigger trigger)
        => new(
            DefaultWorkflowDefinitions.V1Alpha1Schema,
            "plan",
            [new WorkflowDefinitionStep("plan", "builtins.plan")],
            [trigger]);
}
