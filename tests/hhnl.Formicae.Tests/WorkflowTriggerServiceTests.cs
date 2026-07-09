using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowTriggerServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-09T12:00:00Z");

    [Fact]
    public async Task GitHub_issues_labeled_starts_one_workflow_and_audits_it()
    {
        var fixture = await CreateFixtureAsync(DevOpsProviderType.GitHub);

        var started = await fixture.Triggers.HandleIssueLabelEventAsync(Event(
            DevOpsProviderType.GitHub,
            "delivery-1",
            fixture.Repository.RepositoryUrl,
            "https://github.com/acme/widgets/issues/13",
            "formicae"), CancellationToken.None);

        var workflowId = Assert.Single(started);
        var workflow = await fixture.Store.GetWorkflowAsync(workflowId, CancellationToken.None);
        Assert.NotNull(workflow);
        Assert.Equal(fixture.Version.Id, workflow.WorkflowDefinitionVersionId);
        var audit = Assert.Single(await fixture.Store.ListTriggerEventsAsync(workflowId, CancellationToken.None));
        Assert.Equal(workflowId, audit.WorkflowId);
        Assert.Equal("triage", audit.TriggerId);
    }

    [Fact]
    public async Task Gitea_issues_labeled_starts_one_workflow()
    {
        var fixture = await CreateFixtureAsync(DevOpsProviderType.Gitea, "https://gitea.example/acme/widgets");

        var started = await fixture.Triggers.HandleIssueLabelEventAsync(Event(
            DevOpsProviderType.Gitea,
            "delivery-1",
            fixture.Repository.RepositoryUrl,
            "https://gitea.example/acme/widgets/issues/13",
            "formicae"), CancellationToken.None);

        Assert.Single(started);
    }

    [Fact]
    public async Task Duplicate_delivery_and_trigger_does_not_start_another_workflow()
    {
        var fixture = await CreateFixtureAsync(DevOpsProviderType.GitHub);
        var evt = Event(DevOpsProviderType.GitHub, "delivery-1", fixture.Repository.RepositoryUrl, "https://github.com/acme/widgets/issues/13", "formicae");

        Assert.Single(await fixture.Triggers.HandleIssueLabelEventAsync(evt, CancellationToken.None));
        var duplicate = await fixture.Triggers.HandleIssueLabelEventAsync(evt with { IssueUrl = "https://github.com/acme/widgets/issues/14" }, CancellationToken.None);

        Assert.Empty(duplicate);
    }

    [Fact]
    public async Task Duplicate_issue_url_does_not_start_another_workflow()
    {
        var fixture = await CreateFixtureAsync(DevOpsProviderType.GitHub);
        var first = Event(DevOpsProviderType.GitHub, "delivery-1", fixture.Repository.RepositoryUrl, "https://github.com/acme/widgets/issues/13", "formicae");
        var second = first with { DeliveryId = "delivery-2" };

        Assert.Single(await fixture.Triggers.HandleIssueLabelEventAsync(first, CancellationToken.None));
        Assert.Empty(await fixture.Triggers.HandleIssueLabelEventAsync(second, CancellationToken.None));
    }

    [Fact]
    public async Task Nonmatching_label_does_nothing()
    {
        var fixture = await CreateFixtureAsync(DevOpsProviderType.GitHub);

        var started = await fixture.Triggers.HandleIssueLabelEventAsync(Event(
            DevOpsProviderType.GitHub,
            "delivery-1",
            fixture.Repository.RepositoryUrl,
            "https://github.com/acme/widgets/issues/13",
            "other"), CancellationToken.None);

        Assert.Empty(started);
    }

    [Fact]
    public async Task Nonmatching_repository_does_nothing()
    {
        var fixture = await CreateFixtureAsync(DevOpsProviderType.GitHub);

        var started = await fixture.Triggers.HandleIssueLabelEventAsync(Event(
            DevOpsProviderType.GitHub,
            "delivery-1",
            "https://github.com/acme/other",
            "https://github.com/acme/other/issues/13",
            "formicae"), CancellationToken.None);

        Assert.Empty(started);
    }

    [Fact]
    public async Task Repository_default_branch_is_used()
    {
        var fixture = await CreateFixtureAsync(DevOpsProviderType.GitHub, baseBranch: null, repositoryDefaultBranch: "develop");

        var workflowId = Assert.Single(await fixture.Triggers.HandleIssueLabelEventAsync(Event(
            DevOpsProviderType.GitHub,
            "delivery-1",
            fixture.Repository.RepositoryUrl,
            "https://github.com/acme/widgets/issues/13",
            "formicae"), CancellationToken.None));

        var workflow = await fixture.Store.GetWorkflowAsync(workflowId, CancellationToken.None);
        Assert.NotNull(workflow);
        Assert.Equal("develop", workflow.BaseBranch);
    }

    [Fact]
    public async Task Trigger_base_branch_overrides_repository_default_branch()
    {
        var fixture = await CreateFixtureAsync(DevOpsProviderType.GitHub, baseBranch: "release/1", repositoryDefaultBranch: "develop");

        var workflowId = Assert.Single(await fixture.Triggers.HandleIssueLabelEventAsync(Event(
            DevOpsProviderType.GitHub,
            "delivery-1",
            fixture.Repository.RepositoryUrl,
            "https://github.com/acme/widgets/issues/13",
            "formicae"), CancellationToken.None));

        var workflow = await fixture.Store.GetWorkflowAsync(workflowId, CancellationToken.None);
        Assert.NotNull(workflow);
        Assert.Equal("release/1", workflow.BaseBranch);
    }

    private static DevOpsIssueLabelTriggerEvent Event(
        DevOpsProviderType provider,
        string deliveryId,
        string repositoryUrl,
        string issueUrl,
        string label)
        => new(provider, deliveryId, "issues", "labeled", repositoryUrl, issueUrl, label, "acme/widgets");

    private static async Task<Fixture> CreateFixtureAsync(
        DevOpsProviderType provider,
        string repositoryUrl = "https://github.com/acme/widgets",
        string? baseBranch = null,
        string repositoryDefaultBranch = "main")
    {
        var store = new InMemoryWorkflowStore();
        var integrations = new InMemoryDevOpsIntegrationStore();
        var integration = await integrations.CreateAsync(new DevOpsIntegration
        {
            ProviderType = provider,
            DisplayName = provider.ToString(),
            WebhookSecret = "secret",
            WebhookUrl = "https://formicae.example/webhook",
            CreatedAt = Now,
            UpdatedAt = Now
        }, CancellationToken.None);
        var repository = await integrations.AddRepositoryAsync(new ConnectedRepository
        {
            DevOpsIntegrationId = integration.Id,
            Owner = "acme",
            Name = "widgets",
            RepositoryUrl = repositoryUrl,
            DefaultBranch = repositoryDefaultBranch,
            CreatedAt = Now,
            UpdatedAt = Now
        }, CancellationToken.None);

        var validator = new WorkflowDefinitionValidator();
        var definitions = new WorkflowDefinitionService(store, validator, integrations, new FixedClock(Now));
        var definition = await definitions.CreateAsync(new CreateWorkflowDefinitionRequest("Triggered workflow"), CancellationToken.None);
        var version = await definitions.CreateVersionAsync(
            definition.Id,
            new CreateWorkflowDefinitionVersionRequest(null, true, false, new WorkflowDefinitionDocument(
                DefaultWorkflowDefinitions.V1Alpha1Schema,
                "plan",
                [new WorkflowDefinitionStep("plan", "builtins.plan")],
                [new WorkflowDefinitionTrigger("triage", WorkflowTriggerType.DevOpsIssueLabel, true, [repository.Id], "formicae", baseBranch, "gpt-test")])),
            CancellationToken.None);
        var workflows = new WorkflowService(store, clock: new FixedClock(Now), workflowDefinitions: definitions);
        var triggers = new WorkflowTriggerService(store, integrations, workflows, new FixedClock(Now));
        return new Fixture(store, triggers, repository, version);
    }

    private sealed record Fixture(
        InMemoryWorkflowStore Store,
        WorkflowTriggerService Triggers,
        ConnectedRepository Repository,
        WorkflowDefinitionVersionResponse Version);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
