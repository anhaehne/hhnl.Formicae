using System.Reflection;
using System.Text.Json;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class CreateBranchTaskTests
{
    private const string Repository = "https://github.com/acme/repo";
    private static WorkflowDefinitionStep Branch(Guid repositoryId, string id = "branch", string name = "feature/new", string? next = null)
        => new(id, CreateBranchDefinitions.Uses, next, CreateBranch: new(repositoryId, "release/stable", name));

    [Theory]
    [InlineData("feature/new", true)]
    [InlineData("release/1.0", true)]
    [InlineData("main", true)]
    [InlineData("", false)]
    [InlineData("bad name", false)]
    [InlineData("bad..name", false)]
    [InlineData("bad@{name", false)]
    [InlineData("bad.lock", false)]
    [InlineData("bad.lock/child", false)]
    [InlineData(".hidden", false)]
    [InlineData("a//b", false)]
    [InlineData("a/", false)]
    [InlineData("a.", false)]
    [InlineData("-option", false)]
    [InlineData("bad\\name", false)]
    [InlineData("bad~name", false)]
    [InlineData("bad:name", false)]
    [InlineData("bad?name", false)]
    [InlineData("bad*name", false)]
    [InlineData("bad[name", false)]
    public void Branch_names_follow_git_ref_rules(string name, bool valid)
        => Assert.Equal(valid, CreateBranchDefinitions.ValidBranchName(name));

    [Fact]
    public void Definition_rejects_missing_settings_same_branch_and_worker_configuration()
    {
        var step = Branch(Guid.NewGuid());
        Assert.Empty(CreateBranchDefinitions.ValidateStep(step));
        foreach (var invalid in new[] { step with { CreateBranch = null }, step with { CreateBranch = step.CreateBranch! with { RepositoryId = Guid.Empty } },
            step with { CreateBranch = step.CreateBranch! with { BranchName = "release/stable" } }, step with { Model = "model" },
            step with { Uses = "builtins.plan" } })
            Assert.NotEmpty(new WorkflowDefinitionValidator().Validate(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "branch", [invalid])).Errors);
    }

    [Fact]
    public async Task Creates_from_selected_source_and_retains_evidence_without_worker_or_repeating_success()
    {
        var setup = await Setup();
        await setup.Orchestrator.AdvanceAsync(setup.Workflow, default);
        Assert.Equal(WorkflowStatus.Completed, setup.Workflow.Status);
        Assert.Equal(("feature/new", "source-sha"), Assert.Single(setup.Platform.Created));
        Assert.Equal("release/stable", Assert.Single(setup.Platform.ReadHeads));
        var run = Assert.Single(await setup.Store.ListTaskRunsAsync(setup.Workflow.Id, default));
        Assert.Null(run.ExternalId); Assert.Equal(TaskRunStatus.Succeeded, run.Status);
        Assert.Equal(new(Repository, "release/stable", "feature/new", "source-sha"), run.ToResponse().CreateBranchExecution);
        await setup.Orchestrator.AdvanceAsync(setup.Workflow, default);
        Assert.Single(setup.Platform.Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_keeps_source_commit_and_recovers_a_completed_provider_mutation(bool createdBeforeFailure)
    {
        var setup = await Setup(); setup.Platform.Fail = true; setup.Platform.CreateBeforeFailure = createdBeforeFailure;
        await setup.Orchestrator.AdvanceAsync(setup.Workflow, default);
        Assert.Equal(WorkflowStatus.Failed, setup.Workflow.Status);
        var run = Assert.Single(await setup.Store.ListTaskRunsAsync(setup.Workflow.Id, default));
        var pinned = run.CustomTaskExecutionJson;
        Assert.NotNull(pinned);
        await new WorkflowService(setup.Store).RetryWorkflowAsync(setup.Workflow.Id, default);
        setup.Platform.Fail = false; setup.Platform.SourceSha = "moved-source";
        await setup.Orchestrator.AdvanceAsync(setup.Workflow, default);
        Assert.Equal(WorkflowStatus.Completed, setup.Workflow.Status);
        Assert.Equal(("feature/new", "source-sha"), Assert.Single(setup.Platform.Created));
        Assert.Equal(pinned, run.CustomTaskExecutionJson);
        Assert.Equal(pinned, Assert.Single(await setup.Store.ListTaskRunAttemptsAsync(setup.Workflow.Id, default)).CustomTaskExecutionJson);
        Assert.Equal(1, setup.Platform.ReadHeads.Count(name => name == "release/stable"));
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("missing-source")]
    [InlineData("wrong-provider")]
    public async Task Unsafe_or_unavailable_branch_creation_fails_without_mutating(string failure)
    {
        var setup = await Setup(failure == "wrong-provider" ? DevOpsProviderType.Gitea : DevOpsProviderType.GitHub);
        if (failure == "existing") setup.Platform.Heads["feature/new"] = "source-sha";
        if (failure == "missing-source") setup.Platform.Heads.Remove("release/stable");
        await setup.Orchestrator.AdvanceAsync(setup.Workflow, default);
        Assert.Equal(WorkflowStatus.Failed, setup.Workflow.Status); Assert.Empty(setup.Platform.Created);
        Assert.NotEmpty(setup.Workflow.FailureReason!);
    }

    [Fact]
    public async Task Retry_does_not_overwrite_a_destination_at_a_different_commit()
    {
        var setup = await Setup(); setup.Platform.Fail = true;
        await setup.Orchestrator.AdvanceAsync(setup.Workflow, default);
        await new WorkflowService(setup.Store).RetryWorkflowAsync(setup.Workflow.Id, default);
        setup.Platform.Fail = false; setup.Platform.Heads["feature/new"] = "another-commit";
        await setup.Orchestrator.AdvanceAsync(setup.Workflow, default);
        Assert.Equal(WorkflowStatus.Failed, setup.Workflow.Status); Assert.Empty(setup.Platform.Created);
        Assert.Contains("different commit", setup.Workflow.FailureReason);
    }

    [Fact]
    public async Task Graph_executes_each_branch_task_independently()
    {
        var setup = await Setup(graph: true);
        for (var i = 0; i < 5 && setup.Workflow.Status != WorkflowStatus.Completed; i++) await setup.Orchestrator.AdvanceAsync(setup.Workflow, default);
        Assert.Equal(WorkflowStatus.Completed, setup.Workflow.Status);
        Assert.Equal(3, setup.Platform.Created.Count);
        Assert.Equal(3, (await setup.Store.ListTaskRunsAsync(setup.Workflow.Id, default)).Count);
    }

    [Fact]
    public async Task Definition_service_rejects_unknown_and_non_github_repositories()
    {
        var setup = await Setup(DevOpsProviderType.Gitea);
        var service = new WorkflowDefinitionService(setup.Store, new(), setup.Integrations);
        foreach (var id in new[] { setup.RepositoryId, Guid.NewGuid() })
            Assert.False((await service.ValidateAsync(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "branch", [Branch(id)]), default)).IsValid);
    }

    private static async Task<SetupResult> Setup(DevOpsProviderType provider = DevOpsProviderType.GitHub, bool graph = false)
    {
        var integrations = new InMemoryDevOpsIntegrationStore();
        var integration = await integrations.CreateAsync(new() { ProviderType = provider, DisplayName = "Test" }, default);
        var repository = await integrations.AddRepositoryAsync(new() { DevOpsIntegrationId = integration.Id, Owner = "acme", Name = "repo", RepositoryUrl = Repository, DefaultBranch = "main" }, default);
        var store = new InMemoryWorkflowStore();
        IReadOnlyList<WorkflowDefinitionStep> steps = graph ? [Branch(repository.Id, next: "a") with { NextStepIds = ["b"] }, Branch(repository.Id, "a", "feature/a"), Branch(repository.Id, "b", "feature/b")] : [Branch(repository.Id)];
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "branch", steps);
        Assert.True(new WorkflowDefinitionValidator().Validate(document).IsValid);
        var definition = await store.CreateWorkflowDefinitionAsync(new() { Name = "Branches" }, default);
        var version = await store.CreateWorkflowDefinitionVersionAsync(new() { WorkflowDefinitionId = definition.Id, Version = 1, DslSchemaVersion = document.Schema, DefinitionJson = WorkflowDefinitionJson.Serialize(document), IsEnabled = true }, default);
        var summary = await new WorkflowService(store, workflowDefinitions: new WorkflowDefinitionService(store, new())).StartGitHubIssueWorkflowAsync(new(Repository + "/issues/42", Repository, "main", null, definition.Id, version.Id), default);
        var workflow = (await store.GetWorkflowAsync(summary.WorkflowId, default))!;
        var platform = DispatchProxy.Create<IDevOpsPlatform, BranchPlatform>();
        var orchestrator = new WorkflowOrchestrator(store, DispatchProxy.Create<IWorkItemProvider, IssueCommentTaskTests.Forbidden>(),
            DispatchProxy.Create<ISourceControlProvider, IssueCommentTaskTests.Forbidden>(), DispatchProxy.Create<IAgentRunner, IssueCommentTaskTests.Forbidden>(),
            DispatchProxy.Create<IPromptRenderer, IssueCommentTaskTests.Forbidden>(), integrations: integrations, devOpsPlatforms: new Factory(platform, provider, integration, repository));
        return new(store, workflow, orchestrator, (BranchPlatform)(object)platform, integrations, repository.Id);
    }
    private sealed record SetupResult(InMemoryWorkflowStore Store, Workflow Workflow, WorkflowOrchestrator Orchestrator, BranchPlatform Platform, InMemoryDevOpsIntegrationStore Integrations, Guid RepositoryId);
    public class BranchPlatform : DispatchProxy
    {
        public bool Fail, CreateBeforeFailure;
        public string SourceSha { get => Heads["release/stable"]; set => Heads["release/stable"] = value; }
        public Dictionary<string, string> Heads = new() { ["release/stable"] = "source-sha" };
        public List<string> ReadHeads = [];
        public List<(string Name, string Sha)> Created = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case nameof(IDevOpsPlatform.ListBranchesAsync): return Task.FromResult<IReadOnlyList<string>>(Heads.Keys.ToArray());
                case nameof(IDevOpsPlatform.GetBranchHeadShaAsync):
                    var name = (string)args![1]!; ReadHeads.Add(name);
                    return Heads.TryGetValue(name, out var sha) ? Task.FromResult(sha) : Task.FromException<string>(new InvalidOperationException("Source branch unavailable"));
                case nameof(IDevOpsPlatform.CreateBranchAsync):
                    Assert.Null(args![1]); var branch = (string)args[3]!; var source = (string)args[2]!;
                    if (!Fail || CreateBeforeFailure) { Heads.Add(branch, source); Created.Add((branch, source)); }
                    return Fail ? Task.FromException<string>(new InvalidOperationException("provider failure")) : Task.FromResult(branch);
                default: throw new InvalidOperationException("Unexpected API call: " + method.Name);
            }
        }
    }
    private sealed class Factory(IDevOpsPlatform platform, DevOpsProviderType provider, DevOpsIntegration integration, ConnectedRepository repository) : IDevOpsPlatformFactory
    {
        public Task<DevOpsPlatformContext> CreateForRepositoryAsync(string url, CancellationToken token)
        { Assert.Equal(Repository, url); return Task.FromResult(new DevOpsPlatformContext(integration, repository, new(provider, "acme", "repo", url, new("https://github.com")), platform)); }
    }
}
