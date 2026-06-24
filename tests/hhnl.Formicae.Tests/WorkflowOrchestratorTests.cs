using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Infrastructure.Kubernetes;
using hhnl.Formicae.Infrastructure.OpenHands;
using hhnl.Formicae.Infrastructure.Prompts;
using hhnl.Formicae.Tests.TestDoubles;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowOrchestratorTests
{
    [Fact]
    public async Task AdvanceRunnableWorkflows_Completes_fake_vertical_slice()
    {
        var store = new InMemoryWorkflowStore();
        var service = new WorkflowService(store);
        var started = await service.StartGitHubIssueWorkflowAsync(new StartGitHubIssueWorkflowRequest(
            "https://github.com/acme/widgets/issues/42",
            "https://github.com/acme/widgets",
            null,
            "test-model"), CancellationToken.None);
        var orchestrator = new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), new FakeAgentRunner(), new FilePromptRenderer());

        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);
        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);
        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        var workflow = await store.GetWorkflowAsync(started.WorkflowId, CancellationToken.None);
        var runs = await store.ListTaskRunsAsync(started.WorkflowId, CancellationToken.None);

        Assert.NotNull(workflow);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal(WorkflowStep.Done, workflow.CurrentStep);
        Assert.NotNull(workflow.PullRequestUrl);
        Assert.Collection(runs,
            run => Assert.Equal(TaskRunKind.Plan, run.Kind),
            run => Assert.Equal(TaskRunKind.Implement, run.Kind),
            run => Assert.Equal(TaskRunKind.CreatePullRequest, run.Kind));
        Assert.All(runs, run => Assert.Equal(TaskRunStatus.Succeeded, run.Status));
    }

    [Fact]
    public async Task AdvanceRunnableWorkflows_Does_not_duplicate_completed_steps()
    {
        var store = new InMemoryWorkflowStore();
        var workflow = await store.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = "https://github.com/acme/widgets/issues/42",
            RepositoryUrl = "https://github.com/acme/widgets",
            Status = WorkflowStatus.Implementing,
            CurrentStep = WorkflowStep.Implement,
            PlanArtifact = "Existing plan"
        }, CancellationToken.None);
        await store.UpsertTaskRunAsync(new TaskRun
        {
            WorkflowId = workflow.Id,
            Kind = TaskRunKind.Plan,
            Status = TaskRunStatus.Succeeded,
            Output = "Existing plan"
        }, CancellationToken.None);
        var orchestrator = new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), new FakeAgentRunner(), new FilePromptRenderer());

        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);
        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        var runs = await store.ListTaskRunsAsync(workflow.Id, CancellationToken.None);
        Assert.Single(runs, run => run.Kind == TaskRunKind.Plan);
        Assert.Single(runs, run => run.Kind == TaskRunKind.Implement);
        Assert.Single(runs, run => run.Kind == TaskRunKind.CreatePullRequest);
    }

    [Fact]
    public async Task AdvanceRunnableWorkflows_Uses_mock_devops_adapter_for_issue_branch_and_pull_request()
    {
        var store = new InMemoryWorkflowStore();
        var service = new WorkflowService(store);
        var issueUrl = "https://github.com/acme/widgets/issues/99";
        var repositoryUrl = "https://github.com/acme/widgets";
        var devOps = new MockDevOpsAdapter()
            .AddIssue(issueUrl, "Scripted issue", "Scripted issue body", "Scripted comment");
        devOps.DefaultBranchName = "formicae/scripted-branch";
        devOps.DefaultPullRequestUrl = "https://github.com/acme/widgets/pull/123";
        var started = await service.StartGitHubIssueWorkflowAsync(new StartGitHubIssueWorkflowRequest(
            issueUrl,
            repositoryUrl,
            "develop",
            null), CancellationToken.None);
        var orchestrator = new WorkflowOrchestrator(store, devOps, devOps, new FakeAgentRunner(), new FilePromptRenderer());

        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);
        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);
        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        var workflow = await store.GetWorkflowAsync(started.WorkflowId, CancellationToken.None);

        Assert.NotNull(workflow);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal("formicae/scripted-branch", workflow.BranchName);
        Assert.Equal("https://github.com/acme/widgets/pull/123", workflow.PullRequestUrl);
        Assert.Collection(devOps.GetIssueCalls, call => Assert.Equal(issueUrl, call.IssueUrl));
        Assert.Collection(devOps.CreateBranchCalls, call =>
        {
            Assert.Equal(repositoryUrl, call.RepositoryUrl);
            Assert.Equal("develop", call.BaseBranch);
            Assert.Equal(started.WorkflowId, call.WorkflowId);
        });
        Assert.Collection(devOps.CreateDraftPullRequestCalls, call =>
        {
            Assert.Equal(started.WorkflowId, call.WorkflowId);
            Assert.Equal(repositoryUrl, call.RepositoryUrl);
            Assert.Equal("formicae/scripted-branch", call.BranchName);
            Assert.Contains(call.TaskRuns, run => run.Kind == TaskRunKind.Plan);
            Assert.Contains(call.TaskRuns, run => run.Kind == TaskRunKind.Implement);
        });
    }
}

public sealed class AdapterContractTests
{
    [Fact]
    public void Kubernetes_manifest_contains_container_environment_and_command()
    {
        var manifest = KubernetesJobManifest.Render(new KubernetesJobSpec(
            "formicae-plan-test",
            "openhands:test",
            new Dictionary<string, string> { ["LLM_MODEL"] = "test-model" },
            ["openhands", "--headless", "--json", "-t", "Plan this"]));

        Assert.Contains("kind: Job", manifest);
        Assert.Contains("image: openhands:test", manifest);
        Assert.Contains("name: LLM_MODEL", manifest);
        Assert.Contains("--headless", manifest);
        Assert.Contains("--json", manifest);
    }

    [Fact]
    public async Task OpenHands_runner_builds_headless_json_job()
    {
        var jobRunner = new CapturingJobRunner();
        var runner = new OpenHandsAgentRunner(
            jobRunner,
            Options.Create(new KubernetesJobOptions { Image = "openhands:test" }),
            Options.Create(new OpenHandsOptions { DefaultModel = "test-model" }));

        var result = await runner.RunAsync(new AgentTask(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            TaskRunKind.Plan,
            "Plan this",
            "https://github.com/acme/widgets",
            "formicae/test",
            null), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(jobRunner.LastSpec);
        Assert.Equal("openhands:test", jobRunner.LastSpec.Image);
        Assert.Equal(["openhands", "--headless", "--json", "-t", "Plan this"], jobRunner.LastSpec.Command);
        Assert.Equal("test-model", jobRunner.LastSpec.Environment["LLM_MODEL"]);
    }

    private sealed class CapturingJobRunner : IKubernetesJobRunner
    {
        public KubernetesJobSpec? LastSpec { get; private set; }

        public Task<KubernetesJobResult> RunJobAsync(KubernetesJobSpec spec, CancellationToken cancellationToken)
        {
            LastSpec = spec;
            return Task.FromResult(new KubernetesJobResult(true, spec.Name, "ok", null));
        }
    }
}
