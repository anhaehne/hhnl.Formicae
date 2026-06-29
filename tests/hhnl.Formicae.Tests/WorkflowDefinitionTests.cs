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
}
