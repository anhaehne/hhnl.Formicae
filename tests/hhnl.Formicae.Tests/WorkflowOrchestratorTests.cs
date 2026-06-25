using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Infrastructure.GitHub;
using hhnl.Formicae.Infrastructure.Kubernetes;
using hhnl.Formicae.Infrastructure.OpenHands;
using hhnl.Formicae.Infrastructure.Prompts;
using hhnl.Formicae.Tests.TestDoubles;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowOrchestratorTests
{
    [Fact]
    public async Task DiscoverReadyToPlanWorkflows_Queues_labeled_issues()
    {
        var store = new InMemoryWorkflowStore();
        var repositoryUrl = "https://github.com/acme/widgets";
        var issueUrl = "https://github.com/acme/widgets/issues/42";
        var devOps = new MockDevOpsAdapter()
            .AddIssueWithLabels(issueUrl, "Scripted issue", "Scripted issue body", [WorkItemWorkflowLabels.ReadyToPlan]);
        var discovery = new WorkflowDiscoveryService(store, devOps, Options.Create(new WorkflowDiscoveryOptions
        {
            Enabled = true,
            RepositoryUrl = repositoryUrl,
            BaseBranch = "develop",
            Model = "test-model"
        }));

        var discovered = await discovery.DiscoverReadyToPlanWorkflowsAsync(CancellationToken.None);

        var workflows = await store.ListRunnableWorkflowsAsync(CancellationToken.None);
        Assert.Equal(1, discovered);
        var workflow = Assert.Single(workflows);
        Assert.Equal(issueUrl, workflow.IssueUrl);
        Assert.Equal(repositoryUrl, workflow.RepositoryUrl);
        Assert.Equal("develop", workflow.BaseBranch);
        Assert.Equal("test-model", workflow.Model);
        Assert.Equal(WorkflowStatus.Queued, workflow.Status);
        Assert.Equal(WorkflowStep.None, workflow.CurrentStep);
        Assert.Collection(devOps.ListIssuesWithLabelCalls, call =>
        {
            Assert.Equal(repositoryUrl, call.RepositoryUrl);
            Assert.Equal(WorkItemWorkflowLabels.ReadyToPlan, call.Label);
        });
    }

    [Fact]
    public async Task DiscoverReadyToPlanWorkflows_Does_not_duplicate_existing_issue_workflow()
    {
        var store = new InMemoryWorkflowStore();
        var repositoryUrl = "https://github.com/acme/widgets";
        var issueUrl = "https://github.com/acme/widgets/issues/42";
        var devOps = new MockDevOpsAdapter()
            .AddIssueWithLabels(issueUrl, "Scripted issue", "Scripted issue body", [WorkItemWorkflowLabels.ReadyToPlan]);
        var discovery = new WorkflowDiscoveryService(store, devOps, Options.Create(new WorkflowDiscoveryOptions
        {
            Enabled = true,
            RepositoryUrl = repositoryUrl
        }));

        var first = await discovery.DiscoverReadyToPlanWorkflowsAsync(CancellationToken.None);
        var second = await discovery.DiscoverReadyToPlanWorkflowsAsync(CancellationToken.None);

        var workflows = await store.ListRunnableWorkflowsAsync(CancellationToken.None);
        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Single(workflows);
    }
    [Fact]
    public void BuildPlanBody_renders_plan_as_markdown()
    {
        var workflow = new Workflow
        {
            Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
            IssueUrl = "https://github.com/acme/widgets/issues/42",
            RepositoryUrl = "https://github.com/acme/widgets",
            BaseBranch = "main"
        };
        var result = new AgentRunResult(true, "plan-run", "**Implementation Plan**\n\n1. Add the UI.", null);

        var body = PullRequestCommentMarkers.BuildPlanBody(workflow, result);

        Assert.Contains("**Implementation Plan**", body);
        Assert.Contains("1. Add the UI.", body);
        Assert.DoesNotContain("```text", body);
    }
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
            run => Assert.Equal(TaskRunKind.CreatePullRequest, run.Kind),
            run => Assert.Equal(TaskRunKind.AddressComments, run.Kind));
        Assert.All(runs, run => Assert.Equal(TaskRunStatus.Succeeded, run.Status));
    }

    [Fact]
    public async Task AdvanceRunnableWorkflows_Waits_for_ready_to_plan_label()
    {
        var store = new InMemoryWorkflowStore();
        var service = new WorkflowService(store);
        var issueUrl = "https://github.com/acme/widgets/issues/42";
        var devOps = new MockDevOpsAdapter()
            .AddIssueWithLabels(issueUrl, "Scripted issue", "Scripted issue body", []);
        var started = await service.StartGitHubIssueWorkflowAsync(new StartGitHubIssueWorkflowRequest(
            issueUrl,
            "https://github.com/acme/widgets",
            null,
            null), CancellationToken.None);
        var orchestrator = new WorkflowOrchestrator(store, devOps, devOps, new FakeAgentRunner(), new FilePromptRenderer());

        var advanced = await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        var workflow = await store.GetWorkflowAsync(started.WorkflowId, CancellationToken.None);
        var runs = await store.ListTaskRunsAsync(started.WorkflowId, CancellationToken.None);

        Assert.Equal(0, advanced);
        Assert.NotNull(workflow);
        Assert.Equal(WorkflowStatus.Queued, workflow.Status);
        Assert.Equal(WorkflowStep.None, workflow.CurrentStep);
        Assert.Empty(runs);
        Assert.Single(devOps.GetIssueCalls);
        Assert.All(devOps.GetIssueCalls, call => Assert.Equal(issueUrl, call.IssueUrl));
    }

    [Fact]
    public async Task AdvanceRunnableWorkflows_Waits_for_ready_to_implement_label_after_planning()
    {
        var store = new InMemoryWorkflowStore();
        var service = new WorkflowService(store);
        var issueUrl = "https://github.com/acme/widgets/issues/42";
        var devOps = new MockDevOpsAdapter()
            .AddIssueWithLabels(issueUrl, "Scripted issue", "Scripted issue body", [WorkItemWorkflowLabels.ReadyToPlan]);
        var started = await service.StartGitHubIssueWorkflowAsync(new StartGitHubIssueWorkflowRequest(
            issueUrl,
            "https://github.com/acme/widgets",
            null,
            null), CancellationToken.None);
        var orchestrator = new WorkflowOrchestrator(store, devOps, devOps, new FakeAgentRunner(), new FilePromptRenderer());

        var planningAdvanced = await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);
        var implementationAdvanced = await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        var workflow = await store.GetWorkflowAsync(started.WorkflowId, CancellationToken.None);
        var runs = await store.ListTaskRunsAsync(started.WorkflowId, CancellationToken.None);

        Assert.Equal(1, planningAdvanced);
        Assert.Equal(0, implementationAdvanced);
        Assert.NotNull(workflow);
        Assert.Equal(WorkflowStatus.Implementing, workflow.Status);
        Assert.Equal(WorkflowStep.Implement, workflow.CurrentStep);
        Assert.Single(runs, run => run.Kind == TaskRunKind.Plan);
        Assert.DoesNotContain(runs, run => run.Kind == TaskRunKind.Implement);
        Assert.Empty(devOps.CreateBranchCalls);
        Assert.Collection(devOps.UpsertIssueCommentCalls, call =>
        {
            Assert.Equal(issueUrl, call.IssueUrl);
            Assert.Contains(PullRequestCommentMarkers.Plan(started.WorkflowId), call.Body);
            Assert.Contains("Fake Plan output", call.Body);
        });

        devOps.AddIssueWithLabels(
            issueUrl,
            "Scripted issue",
            "Scripted issue body",
            [WorkItemWorkflowLabels.ReadyToPlan, WorkItemWorkflowLabels.ReadyToImplement]);

        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        runs = await store.ListTaskRunsAsync(started.WorkflowId, CancellationToken.None);

        Assert.Contains(runs, run => run.Kind == TaskRunKind.Implement && run.Status == TaskRunStatus.Succeeded);
        Assert.Single(devOps.CreateBranchCalls);
    }


    [Fact]
    public async Task AdvanceRunnableWorkflows_Updates_existing_plan_comment_for_completed_plan_retry()
    {
        var store = new InMemoryWorkflowStore();
        var workflow = await store.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = "https://github.com/acme/widgets/issues/42",
            RepositoryUrl = "https://github.com/acme/widgets",
            Status = WorkflowStatus.Planning,
            CurrentStep = WorkflowStep.Plan
        }, CancellationToken.None);
        await store.UpsertTaskRunAsync(new TaskRun
        {
            WorkflowId = workflow.Id,
            Kind = TaskRunKind.Plan,
            Status = TaskRunStatus.Succeeded,
            ExternalId = "existing-plan",
            Output = "Existing plan output"
        }, CancellationToken.None);
        var devOps = new MockDevOpsAdapter();
        var orchestrator = new WorkflowOrchestrator(store, devOps, devOps, new FakeAgentRunner(), new FilePromptRenderer());

        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);
        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        Assert.Collection(devOps.UpsertIssueCommentCalls, call =>
        {
            Assert.Equal(workflow.IssueUrl, call.IssueUrl);
            Assert.Equal(PullRequestCommentMarkers.Plan(workflow.Id), call.Marker);
            Assert.Contains("Existing plan output", call.Body);
        });
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

        var workflowAfterAdvance = await store.GetWorkflowAsync(workflow.Id, CancellationToken.None);
        var runs = await store.ListTaskRunsAsync(workflow.Id, CancellationToken.None);

        Assert.Single(runs, run => run.Kind == TaskRunKind.Plan);
        Assert.Single(runs, run => run.Kind == TaskRunKind.Implement);
        Assert.Single(runs, run => run.Kind == TaskRunKind.CreatePullRequest);
        Assert.DoesNotContain(runs, run => run.Kind == TaskRunKind.AddressComments);
        Assert.NotNull(workflowAfterAdvance);
        Assert.Equal(WorkflowStatus.Reviewing, workflowAfterAdvance.Status);
    }

    [Fact]
    public async Task AdvanceRunnableWorkflows_Uses_mock_devops_adapter_for_issue_branch_pull_request_and_comments()
    {
        var store = new InMemoryWorkflowStore();
        var service = new WorkflowService(store);
        var issueUrl = "https://github.com/acme/widgets/issues/99";
        var repositoryUrl = "https://github.com/acme/widgets";
        var devOps = new MockDevOpsAdapter()
            .AddIssue(issueUrl, "Scripted issue", "Scripted issue body", "Scripted comment")
            .AddPullRequestComment("1", "reviewer", "Please address this before merging.", PullRequestCommentKind.ReviewComment);
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
        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        var workflow = await store.GetWorkflowAsync(started.WorkflowId, CancellationToken.None);
        var runs = await store.ListTaskRunsAsync(started.WorkflowId, CancellationToken.None);

        Assert.NotNull(workflow);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal("formicae/scripted-branch", workflow.BranchName);
        Assert.Equal("https://github.com/acme/widgets/pull/123", workflow.PullRequestUrl);
        Assert.Equal(2, devOps.GetIssueCalls.Count);
        Assert.All(devOps.GetIssueCalls, call => Assert.Equal(issueUrl, call.IssueUrl));
        Assert.Collection(devOps.UpsertIssueCommentCalls, call =>
        {
            Assert.Equal(issueUrl, call.IssueUrl);
            Assert.Equal(PullRequestCommentMarkers.Plan(started.WorkflowId), call.Marker);
            Assert.Contains("Fake Plan output", call.Body);
        });

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
        Assert.Collection(devOps.ReactToIssueCalls,
            call =>
            {
                Assert.Equal(issueUrl, call.IssueUrl);
                Assert.Equal(WorkflowReactionContent.Started, call.Reaction);
            },
            call =>
            {
                Assert.Equal(issueUrl, call.IssueUrl);
                Assert.Equal(WorkflowReactionContent.Started, call.Reaction);
            });
        Assert.Contains(runs, run => run.Kind == TaskRunKind.AddressComments && run.Status == TaskRunStatus.Succeeded);
        Assert.Collection(devOps.ListPullRequestCommentsCalls, call =>
        {
            Assert.Equal(started.WorkflowId, call.WorkflowId);
            Assert.Equal("https://github.com/acme/widgets/pull/123", call.PullRequestUrl);
        });
        Assert.Collection(devOps.ReactToPullRequestCommentCalls, call =>
        {
            Assert.Equal(started.WorkflowId, call.WorkflowId);
            Assert.Equal("1", call.CommentId);
            Assert.Equal(PullRequestCommentKind.ReviewComment, call.Kind);
            Assert.Equal(WorkflowReactionContent.Started, call.Reaction);
        });
        Assert.Collection(devOps.UpsertPullRequestCommentCalls, call =>
        {
            Assert.Equal(started.WorkflowId, call.WorkflowId);
            Assert.Equal("https://github.com/acme/widgets/pull/123", call.PullRequestUrl);
            Assert.Contains(PullRequestCommentMarkers.AddressComments(started.WorkflowId), call.Body);
            Assert.Contains("Fake AddressComments output", call.Body);
            Assert.DoesNotContain("```text", call.Body);
        });
    }

    [Fact]
    public async Task AdvanceRunnableWorkflows_Leaves_pull_request_in_reviewing_when_no_comments_exist()
    {
        var store = new InMemoryWorkflowStore();
        var service = new WorkflowService(store);
        var issueUrl = "https://github.com/acme/widgets/issues/100";
        var devOps = new MockDevOpsAdapter()
            .AddIssue(issueUrl, "Scripted issue", "Scripted issue body")
            .AddPullRequestComment("formicae", "automation", PullRequestCommentMarkers.AddressComments(Guid.Empty));
        var started = await service.StartGitHubIssueWorkflowAsync(new StartGitHubIssueWorkflowRequest(
            issueUrl,
            "https://github.com/acme/widgets",
            null,
            null), CancellationToken.None);
        var orchestrator = new WorkflowOrchestrator(store, devOps, devOps, new FakeAgentRunner(), new FilePromptRenderer());

        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);
        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);
        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);
        var advanced = await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        var workflow = await store.GetWorkflowAsync(started.WorkflowId, CancellationToken.None);
        var runs = await store.ListTaskRunsAsync(started.WorkflowId, CancellationToken.None);

        Assert.Equal(0, advanced);
        Assert.NotNull(workflow);
        Assert.Equal(WorkflowStatus.Reviewing, workflow.Status);
        Assert.Equal(WorkflowStep.AddressComments, workflow.CurrentStep);
        Assert.DoesNotContain(runs, run => run.Kind == TaskRunKind.AddressComments);
        Assert.Collection(devOps.ListPullRequestCommentsCalls, call =>
        {
            Assert.Equal(started.WorkflowId, call.WorkflowId);
            Assert.Equal(devOps.DefaultPullRequestUrl, call.PullRequestUrl);
        });
    }


    [Fact]
    public async Task AdvanceRunnableWorkflows_Reruns_address_comments_for_newer_comments_after_completed_pass()
    {
        var store = new InMemoryWorkflowStore();
        var previousAddressedAt = DateTimeOffset.Parse("2026-06-25T16:01:41Z");
        var devOps = new MockDevOpsAdapter();
        devOps.DefaultPullRequestUrl = "https://github.com/acme/widgets/pull/23";
        devOps
            .AddPullRequestComment("old", "reviewer", "Already addressed.", PullRequestCommentKind.IssueComment, previousAddressedAt.AddMinutes(-5))
            .AddPullRequestComment("new", "reviewer", "Anything else to add?", PullRequestCommentKind.IssueComment, previousAddressedAt.AddMinutes(95));
        var workflow = await store.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = "https://github.com/acme/widgets/issues/2",
            RepositoryUrl = "https://github.com/acme/widgets",
            BranchName = "formicae/comment-follow-up",
            PullRequestUrl = devOps.DefaultPullRequestUrl,
            Status = WorkflowStatus.Reviewing,
            CurrentStep = WorkflowStep.AddressComments
        }, CancellationToken.None);
        await store.UpsertTaskRunAsync(new TaskRun
        {
            WorkflowId = workflow.Id,
            Kind = TaskRunKind.AddressComments,
            Status = TaskRunStatus.Succeeded,
            Output = "Previous address-comments output",
            UpdatedAt = previousAddressedAt
        }, CancellationToken.None);
        var orchestrator = new WorkflowOrchestrator(store, devOps, devOps, new FakeAgentRunner(), new FilePromptRenderer());

        var advanced = await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        var updated = await store.GetWorkflowAsync(workflow.Id, CancellationToken.None);
        var run = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.AddressComments, CancellationToken.None);
        Assert.Equal(1, advanced);
        Assert.NotNull(updated);
        Assert.Equal(WorkflowStatus.Completed, updated.Status);
        Assert.Equal(WorkflowStep.Done, updated.CurrentStep);
        Assert.NotNull(run);
        Assert.Equal(TaskRunStatus.Succeeded, run.Status);
        Assert.Contains("Fake AddressComments output", run.Output);
        Assert.Collection(devOps.ReactToPullRequestCommentCalls, call =>
        {
            Assert.Equal(workflow.Id, call.WorkflowId);
            Assert.Equal("new", call.CommentId);
            Assert.Equal(WorkflowReactionContent.Started, call.Reaction);
        });
        Assert.Collection(devOps.UpsertPullRequestCommentCalls, call =>
        {
            Assert.Equal(workflow.Id, call.WorkflowId);
            Assert.Contains("Fake AddressComments output", call.Body);
        });
    }

    [Fact]
    public async Task AdvanceRunnableWorkflows_Fails_workflow_when_address_comments_agent_fails()
    {
        var store = new InMemoryWorkflowStore();
        var service = new WorkflowService(store);
        var issueUrl = "https://github.com/acme/widgets/issues/101";
        var devOps = new MockDevOpsAdapter()
            .AddIssue(issueUrl, "Scripted issue", "Scripted issue body")
            .AddPullRequestComment("1", "reviewer", "Please fix this.", PullRequestCommentKind.ReviewComment);
        var started = await service.StartGitHubIssueWorkflowAsync(new StartGitHubIssueWorkflowRequest(
            issueUrl,
            "https://github.com/acme/widgets",
            null,
            null), CancellationToken.None);
        var orchestrator = new WorkflowOrchestrator(store, devOps, devOps, new FailingAddressCommentsAgentRunner(), new FilePromptRenderer());

        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);
        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);
        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);
        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        var workflow = await store.GetWorkflowAsync(started.WorkflowId, CancellationToken.None);
        var runs = await store.ListTaskRunsAsync(started.WorkflowId, CancellationToken.None);

        Assert.NotNull(workflow);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status);
        Assert.Equal("address comments failed", workflow.FailureReason);
        Assert.Contains(runs, run => run.Kind == TaskRunKind.AddressComments && run.Status == TaskRunStatus.Failed);
    }

    [Fact]
    public async Task FilePromptRenderer_Renders_pull_request_comments_for_address_comments_task()
    {
        var workflow = new Workflow
        {
            IssueUrl = "https://github.com/acme/widgets/issues/42",
            RepositoryUrl = "https://github.com/acme/widgets",
            BranchName = "formicae/comment-fix",
            PullRequestUrl = "https://github.com/acme/widgets/pull/123",
            PlanArtifact = "Existing plan"
        };
        var comments = new[]
        {
            new PullRequestComment(
                "review:1",
                "reviewer",
                "Please cover this edge case.",
                "https://github.com/acme/widgets/pull/123#discussion_r1",
                DateTimeOffset.Parse("2026-06-25T10:11:12Z"),
                PullRequestCommentKind.ReviewComment)
        };

        var prompt = await new FilePromptRenderer().RenderAsync(TaskRunKind.AddressComments, workflow, null, comments, CancellationToken.None);

        Assert.Contains("formicae/comment-fix", prompt);
        Assert.Contains("[ReviewComment] reviewer at 2026-06-25T10:11:12.0000000+00:00", prompt);
        Assert.Contains("URL: https://github.com/acme/widgets/pull/123#discussion_r1", prompt);
        Assert.Contains("Please cover this edge case.", prompt);
    }

    [Fact]
    public async Task GitHubSourceControlProvider_Adds_implementation_summary_to_created_pull_request_body()
    {
        var previousToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "test-token");
        try
        {
            var handler = new CreatePullRequestGitHubHandler();
            var provider = new GitHubSourceControlProvider(new HttpClient(handler));
            var workflow = new Workflow
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                RepositoryUrl = "https://github.com/acme/widgets",
                IssueUrl = "https://github.com/acme/widgets/issues/42",
                BaseBranch = "main",
                BranchName = "formicae/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            };
            var runs = new[]
            {
                new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.Plan, Status = TaskRunStatus.Succeeded, Output = "Plan output" },
                new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.Implement, Status = TaskRunStatus.Succeeded, Output = "Implemented the management UI and recent workflow API." }
            };

            var result = await provider.CreateDraftPullRequestAsync(workflow, runs, CancellationToken.None);

            Assert.Equal("https://github.com/acme/widgets/pull/123", result.Url);
            Assert.Contains("## Implementation Summary", handler.CreatedPullRequestBody);
            Assert.Contains("Implemented the management UI and recent workflow API.", handler.CreatedPullRequestBody);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", previousToken);
        }
    }
    [Fact]
    public async Task GitHubWorkItemProvider_Reacts_to_issue()
    {
        var previousToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "test-token");
        try
        {
            var handler = new ReactionGitHubHandler();
            var provider = new GitHubWorkItemProvider(new HttpClient(handler));

            await provider.ReactToIssueAsync(
                "https://github.com/acme/widgets/issues/42",
                WorkflowReactionContent.Started,
                CancellationToken.None);

            Assert.Equal(["POST https://api.github.com/repos/acme/widgets/issues/42/reactions"], handler.Requests.Select(request => $"{request.Method} {request.RequestUri}"));
            Assert.Equal([WorkflowReactionContent.Started], handler.Reactions);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", previousToken);
        }
    }
    [Fact]
    public async Task GitHubSourceControlProvider_Lists_issue_and_review_comments_for_pull_request()
    {
        var previousToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "test-token");
        try
        {
            var handler = new CapturingGitHubHandler();
            var provider = new GitHubSourceControlProvider(new HttpClient(handler));
            var workflow = new Workflow
            {
                RepositoryUrl = "https://github.com/acme/widgets",
                IssueUrl = "https://github.com/acme/widgets/issues/42",
                PullRequestUrl = "https://github.com/acme/widgets/pull/123"
            };

            var comments = await provider.ListPullRequestCommentsAsync(workflow, CancellationToken.None);

            Assert.Equal([
                "https://api.github.com/repos/acme/widgets/issues/123/comments",
                "https://api.github.com/repos/acme/widgets/pulls/123/comments"
            ], handler.Requests.Select(request => request.RequestUri?.ToString()));
            Assert.All(handler.Requests, request => Assert.Equal("Bearer", request.Headers.Authorization?.Scheme));
            Assert.Collection(comments,
                comment =>
                {
                    Assert.Equal("issue:10", comment.Id);
                    Assert.Equal("maintainer", comment.Author);
                    Assert.Equal("Please update the docs.", comment.Body);
                    Assert.Equal("https://github.com/acme/widgets/pull/123#issuecomment-10", comment.Url);
                    Assert.Equal(PullRequestCommentKind.IssueComment, comment.Kind);
                },
                comment =>
                {
                    Assert.Equal("review:20", comment.Id);
                    Assert.Equal("reviewer", comment.Author);
                    Assert.Equal("Please add a regression test.", comment.Body);
                    Assert.Equal("https://github.com/acme/widgets/pull/123#discussion_r20", comment.Url);
                    Assert.Equal(PullRequestCommentKind.ReviewComment, comment.Kind);
                });
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", previousToken);
        }
    }

    [Fact]
    public async Task GitHubSourceControlProvider_Creates_marked_pull_request_comment_when_missing()
    {
        var previousToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "test-token");
        try
        {
            var handler = new UpsertGitHubCommentHandler(existingMarkedCommentId: null);
            var provider = new GitHubSourceControlProvider(new HttpClient(handler));
            var workflow = new Workflow
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                RepositoryUrl = "https://github.com/acme/widgets",
                IssueUrl = "https://github.com/acme/widgets/issues/42",
                PullRequestUrl = "https://github.com/acme/widgets/pull/123"
            };
            var body = PullRequestCommentMarkers.BuildAddressCommentsBody(
                workflow,
                new AgentRunResult(true, "run-1", "Addressed the requested changes.", null));

            await provider.UpsertPullRequestCommentAsync(workflow, body, CancellationToken.None);

            Assert.Equal([
                "GET https://api.github.com/repos/acme/widgets/issues/123/comments",
                "POST https://api.github.com/repos/acme/widgets/issues/123/comments"
            ], handler.Requests.Select(request => $"{request.Method} {request.RequestUri}"));
            Assert.Single(handler.RequestBodies, requestBody => requestBody.Contains(PullRequestCommentMarkers.AddressComments(workflow.Id)));
            Assert.Single(handler.RequestBodies, requestBody => requestBody.Contains("Addressed the requested changes."));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", previousToken);
        }
    }

    [Fact]
    public async Task GitHubSourceControlProvider_Reacts_to_issue_and_review_comments()
    {
        var previousToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "test-token");
        try
        {
            var handler = new ReactionGitHubHandler();
            var provider = new GitHubSourceControlProvider(new HttpClient(handler));
            var workflow = new Workflow
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                RepositoryUrl = "https://github.com/acme/widgets",
                IssueUrl = "https://github.com/acme/widgets/issues/42",
                PullRequestUrl = "https://github.com/acme/widgets/pull/123"
            };

            await provider.ReactToPullRequestCommentAsync(
                workflow,
                new PullRequestComment("issue:10", "maintainer", "Please update docs.", "https://github.com/acme/widgets/pull/123#issuecomment-10", DateTimeOffset.UtcNow, PullRequestCommentKind.IssueComment),
                WorkflowReactionContent.Started,
                CancellationToken.None);
            await provider.ReactToPullRequestCommentAsync(
                workflow,
                new PullRequestComment("review:20", "reviewer", "Please add a test.", "https://github.com/acme/widgets/pull/123#discussion_r20", DateTimeOffset.UtcNow, PullRequestCommentKind.ReviewComment),
                WorkflowReactionContent.Started,
                CancellationToken.None);

            Assert.Equal([
                "POST https://api.github.com/repos/acme/widgets/issues/comments/10/reactions",
                "POST https://api.github.com/repos/acme/widgets/pulls/comments/20/reactions"
            ], handler.Requests.Select(request => $"{request.Method} {request.RequestUri}"));
            Assert.Equal([WorkflowReactionContent.Started, WorkflowReactionContent.Started], handler.Reactions);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", previousToken);
        }
    }
    [Fact]
    public async Task GitHubSourceControlProvider_Updates_marked_pull_request_comment_when_present()
    {
        var previousToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "test-token");
        try
        {
            var handler = new UpsertGitHubCommentHandler(existingMarkedCommentId: 77);
            var provider = new GitHubSourceControlProvider(new HttpClient(handler));
            var workflow = new Workflow
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                RepositoryUrl = "https://github.com/acme/widgets",
                IssueUrl = "https://github.com/acme/widgets/issues/42",
                PullRequestUrl = "https://github.com/acme/widgets/pull/123"
            };
            var body = PullRequestCommentMarkers.BuildAddressCommentsBody(
                workflow,
                new AgentRunResult(true, "run-1", "Updated summary.", null));

            await provider.UpsertPullRequestCommentAsync(workflow, body, CancellationToken.None);

            Assert.Equal([
                "GET https://api.github.com/repos/acme/widgets/issues/123/comments",
                "PATCH https://api.github.com/repos/acme/widgets/issues/comments/77"
            ], handler.Requests.Select(request => $"{request.Method} {request.RequestUri}"));
            Assert.Single(handler.RequestBodies, requestBody => requestBody.Contains(PullRequestCommentMarkers.AddressComments(workflow.Id)));
            Assert.Single(handler.RequestBodies, requestBody => requestBody.Contains("Updated summary."));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", previousToken);
        }
    }

    private sealed class FailingAddressCommentsAgentRunner : IAgentRunner
    {
        public Task<AgentRunResult> RunAsync(AgentTask task, CancellationToken cancellationToken)
        {
            if (task.Kind == TaskRunKind.AddressComments)
            {
                return Task.FromResult(new AgentRunResult(false, "address-comments-run", "failed", "address comments failed"));
            }

            return Task.FromResult(new AgentRunResult(true, $"fake-{task.Kind.ToString().ToLowerInvariant()}", $"Fake {task.Kind} output.", null));
        }
    }

    private sealed class CreatePullRequestGitHubHandler : HttpMessageHandler
    {
        public string CreatedPullRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/repos/acme/widgets/contents/.formicae/workflows/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.md")
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{}")
                };
            }

            if (request.Method == HttpMethod.Put && path == "/repos/acme/widgets/contents/.formicae/workflows/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.md")
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                };
            }

            if (request.Method == HttpMethod.Get && path == "/repos/acme/widgets/pulls")
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("[]")
                };
            }

            if (request.Method == HttpMethod.Post && path == "/repos/acme/widgets/pulls")
            {
                var requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = System.Text.Json.JsonDocument.Parse(requestJson);
                CreatedPullRequestBody = document.RootElement.GetProperty("body").GetString() ?? string.Empty;
                return new HttpResponseMessage(System.Net.HttpStatusCode.Created)
                {
                    Content = new StringContent("""
                        { "html_url": "https://github.com/acme/widgets/pull/123" }
                        """)
                };
            }

            throw new InvalidOperationException($"Unexpected GitHub request: {request.Method} {path}");
        }
    }
    private sealed class ReactionGitHubHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Reactions { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null)
            {
                var requestJson = await request.Content.ReadAsStringAsync(cancellationToken);
                using var document = System.Text.Json.JsonDocument.Parse(requestJson);
                Reactions.Add(document.RootElement.GetProperty("content").GetString() ?? string.Empty);
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.Created)
            {
                Content = new StringContent("{}")
            };
        }
    }
    private sealed class UpsertGitHubCommentHandler(long? existingMarkedCommentId) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null)
            {
                var requestJson = await request.Content.ReadAsStringAsync(cancellationToken);
                using var document = System.Text.Json.JsonDocument.Parse(requestJson);
                RequestBodies.Add(document.RootElement.GetProperty("body").GetString() ?? string.Empty);
            }

            var path = request.RequestUri?.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/repos/acme/widgets/issues/123/comments")
            {
                var json = existingMarkedCommentId is null
                    ? "[]"
                    : $$"""
                      [
                        {
                          "id": {{existingMarkedCommentId}},
                          "body": "<!-- formicae:workflow:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:address-comments --> previous summary",
                          "html_url": "https://github.com/acme/widgets/pull/123#issuecomment-{{existingMarkedCommentId}}",
                          "updated_at": "2026-06-25T10:00:00Z",
                          "user": { "login": "automation" }
                        }
                      ]
                      """;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                };
            }

            if (request.Method == HttpMethod.Post && path == "/repos/acme/widgets/issues/123/comments")
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.Created)
                {
                    Content = new StringContent("{}")
                };
            }

            if (request.Method == HttpMethod.Patch && path == $"/repos/acme/widgets/issues/comments/{existingMarkedCommentId}")
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                };
            }

            throw new InvalidOperationException($"Unexpected GitHub request: {request.Method} {path}");
        }
    }

    private sealed class CapturingGitHubHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            var path = request.RequestUri?.AbsolutePath;
            var json = path switch
            {
                "/repos/acme/widgets/issues/123/comments" =>
                    """
                    [
                      {
                        "id": 10,
                        "body": "Please update the docs.",
                        "html_url": "https://github.com/acme/widgets/pull/123#issuecomment-10",
                        "updated_at": "2026-06-25T10:00:00Z",
                        "user": { "login": "maintainer" }
                      },
                      {
                        "id": 11,
                        "body": "   ",
                        "html_url": "https://github.com/acme/widgets/pull/123#issuecomment-11",
                        "updated_at": "2026-06-25T10:01:00Z",
                        "user": { "login": "maintainer" }
                      },
                      {
                        "id": 12,
                        "body": "<!-- formicae:workflow:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:address-comments --> automated summary",
                        "html_url": "https://github.com/acme/widgets/pull/123#issuecomment-12",
                        "updated_at": "2026-06-25T10:01:30Z",
                        "user": { "login": "maintainer" }
                      }
                    ]
                    """,
                "/repos/acme/widgets/pulls/123/comments" =>
                    """
                    [
                      {
                        "id": 20,
                        "body": "Please add a regression test.",
                        "html_url": "https://github.com/acme/widgets/pull/123#discussion_r20",
                        "updated_at": "2026-06-25T10:02:00Z",
                        "user": { "login": "reviewer" }
                      }
                    ]
                    """,
                _ => throw new InvalidOperationException($"Unexpected GitHub request path: {path}")
            };

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
        }
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
        Assert.Equal(KubernetesJobAuthMethods.ApiKey, jobRunner.LastSpec.AuthMethod);
        Assert.Equal(["/bin/sh", "-lc", "openhands --headless --json --override-with-envs -t \"$FORMICAE_TASK_PROMPT\""], jobRunner.LastSpec.Command);
        Assert.Equal("test-model", jobRunner.LastSpec.Environment["LLM_MODEL"]);
        Assert.Equal("Plan this", jobRunner.LastSpec.Environment["FORMICAE_TASK_PROMPT"]);
        Assert.Equal(OpenHandsAuthMethods.ApiKey, jobRunner.LastSpec.Environment["FORMICAE_OPENHANDS_AUTH_METHOD"]);
    }

    [Fact]
    public async Task OpenHands_runner_extracts_final_agent_message_from_json_logs()
    {
        var jobRunner = new CapturingJobRunner
        {
            Result = new KubernetesJobResult(true, "formicae-plan-test", """
                apiVersion: batch/v1
                kind: Job
                --- pod/formicae-plan-test-pod logs ---
                {"type":"thread.started","thread_id":"thread-1"}
                {"type":"item.completed","item":{"type":"agent_message","text":"I am inspecting the issue."}}
                {"type":"item.completed","item":{"type":"command_execution","command":"rg TODO"}}
                {"type":"item.completed","item":{"type":"agent_message","text":"Implementation plan:\n1. Add the management UI.\n2. Cover it with tests."}}
                """, null)
        };
        var runner = new OpenHandsAgentRunner(
            jobRunner,
            Options.Create(new KubernetesJobOptions { Image = "openhands:test" }),
            Options.Create(new OpenHandsOptions { DefaultModel = "test-model" }));

        var result = await runner.RunAsync(new AgentTask(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            TaskRunKind.Plan,
            "Plan this",
            "https://github.com/acme/widgets",
            "formicae/test",
            null), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("Implementation plan:\n1. Add the management UI.\n2. Cover it with tests.", result.Output);
        Assert.DoesNotContain("apiVersion", result.Output);
        Assert.DoesNotContain("command_execution", result.Output);
    }

    [Fact]
    public async Task OpenHands_runner_uses_codex_subscription_command_when_configured()
    {
        var jobRunner = new CapturingJobRunner();
        var runner = new OpenHandsAgentRunner(
            jobRunner,
            Options.Create(new KubernetesJobOptions { Image = "python:3.12-slim" }),
            Options.Create(new OpenHandsOptions
            {
                AuthMethod = OpenHandsAuthMethods.CodexSubscription,
                DefaultModel = "gpt-5.2-codex",
                CodexSubscriptionImage = "node:22-bookworm-slim",
                CodexSubscriptionBootstrapCommand = "install git",
                CodexSubscriptionCommand = "run codex"
            }));

        var result = await runner.RunAsync(new AgentTask(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            TaskRunKind.Implement,
            "Implement this",
            "https://github.com/acme/widgets",
            "formicae/test",
            null), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(jobRunner.LastSpec);
        Assert.Equal("node:22-bookworm-slim", jobRunner.LastSpec.Image);
        Assert.Equal(KubernetesJobAuthMethods.CodexSubscription, jobRunner.LastSpec.AuthMethod);
        Assert.Equal(["/bin/sh", "-lc", "install git && run codex"], jobRunner.LastSpec.Command);
        Assert.Equal(OpenHandsAuthMethods.CodexSubscription, jobRunner.LastSpec.Environment["FORMICAE_OPENHANDS_AUTH_METHOD"]);
        Assert.Equal("gpt-5.2-codex", jobRunner.LastSpec.Environment["FORMICAE_MODEL"]);
        Assert.Equal("""{"model":"gpt-5.2-codex"}""", jobRunner.LastSpec.Environment["CODEX_CONFIG"]);
        Assert.False(jobRunner.LastSpec.Environment.ContainsKey("LLM_MODEL"));
    }

    [Fact]
    public async Task OpenHands_runner_allows_codex_subscription_without_configured_model()
    {
        var jobRunner = new CapturingJobRunner();
        var runner = new OpenHandsAgentRunner(
            jobRunner,
            Options.Create(new KubernetesJobOptions { Image = "python:3.12-slim" }),
            Options.Create(new OpenHandsOptions
            {
                AuthMethod = OpenHandsAuthMethods.CodexSubscription,
                DefaultModel = null,
                CodexSubscriptionImage = "node:22-bookworm-slim",
                CodexSubscriptionCommand = "run codex"
            }));

        var result = await runner.RunAsync(new AgentTask(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            TaskRunKind.Plan,
            "Plan this",
            "https://github.com/acme/widgets",
            "formicae/test",
            null), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(jobRunner.LastSpec);
        Assert.Equal(string.Empty, jobRunner.LastSpec.Environment["FORMICAE_MODEL"]);
        Assert.False(jobRunner.LastSpec.Environment.ContainsKey("CODEX_CONFIG"));
        Assert.False(jobRunner.LastSpec.Environment.ContainsKey("LLM_MODEL"));
    }

    [Fact]
    public async Task OpenHands_runner_rejects_unknown_auth_method()
    {
        var runner = new OpenHandsAgentRunner(
            new CapturingJobRunner(),
            Options.Create(new KubernetesJobOptions { Image = "openhands:test" }),
            Options.Create(new OpenHandsOptions { AuthMethod = "Unknown" }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(new AgentTask(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            TaskRunKind.Plan,
            "Plan this",
            "https://github.com/acme/widgets",
            "formicae/test",
            null), CancellationToken.None));

        Assert.Contains("Unsupported OpenHands auth method", exception.Message);
    }

    [Fact]
    public async Task Kubernetes_runner_creates_api_key_job_and_collects_pod_logs()
    {
        var api = new CapturingKubernetesJobApi
        {
            Statuses = new Queue<k8s.Models.V1Job>([
                new k8s.Models.V1Job
                {
                    Status = new k8s.Models.V1JobStatus { Succeeded = 1 }
                }
            ]),
            Pods =
            [
                new k8s.Models.V1Pod
                {
                    Metadata = new k8s.Models.V1ObjectMeta
                    {
                        Name = "formicae-plan-test-pod",
                        CreationTimestamp = DateTime.UtcNow
                    }
                }
            ],
            PodLogs = "agent output"
        };
        var runner = new KubernetesJobRunner(api, Options.Create(new KubernetesJobOptions
        {
            Namespace = "formicae",
            PollIntervalSeconds = 1,
            TimeoutSeconds = 5,
            RuntimeSecretName = "formicae-runtime-secrets",
            LlmApiKeySecretName = "openhands-llm-api-key",
            CodexAuthSecretName = "formicae-codex-auth"
        }));

        var result = await runner.RunJobAsync(new KubernetesJobSpec(
            "formicae-plan-test",
            "openhands:test",
            new Dictionary<string, string> { ["LLM_MODEL"] = "test-model" },
            ["openhands", "--headless", "--json", "-t", "Plan this"]), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.FailureReason);
        Assert.Contains("agent output", result.Logs);
        Assert.NotNull(api.CreatedJob);
        Assert.Equal("formicae-plan-test", api.CreatedJob.Metadata.Name);
        var container = Assert.Single(api.CreatedJob.Spec.Template.Spec.Containers);
        Assert.Equal("openhands:test", container.Image);
        Assert.Contains(container.Env, env => env.Name == "LLM_MODEL" && env.Value == "test-model");
        Assert.Contains(container.EnvFrom, source => source.SecretRef.Name == "formicae-runtime-secrets");
        Assert.Contains(container.EnvFrom, source => source.SecretRef.Name == "openhands-llm-api-key");
        Assert.Null(container.VolumeMounts);
        Assert.Null(api.CreatedJob.Spec.Template.Spec.Volumes);
    }

    [Fact]
    public async Task Kubernetes_runner_creates_codex_subscription_job_with_codex_auth_only()
    {
        var api = new CapturingKubernetesJobApi
        {
            Statuses = new Queue<k8s.Models.V1Job>([
                new k8s.Models.V1Job
                {
                    Status = new k8s.Models.V1JobStatus { Succeeded = 1 }
                }
            ]),
            Pods =
            [
                new k8s.Models.V1Pod
                {
                    Metadata = new k8s.Models.V1ObjectMeta
                    {
                        Name = "formicae-plan-test-pod",
                        CreationTimestamp = DateTime.UtcNow
                    }
                }
            ],
            PodLogs = "agent output"
        };
        var runner = new KubernetesJobRunner(api, Options.Create(new KubernetesJobOptions
        {
            Namespace = "formicae",
            PollIntervalSeconds = 1,
            TimeoutSeconds = 5,
            RuntimeSecretName = "formicae-runtime-secrets",
            LlmApiKeySecretName = "openhands-llm-api-key",
            CodexAuthSecretName = "formicae-codex-auth"
        }));

        var result = await runner.RunJobAsync(new KubernetesJobSpec(
            "formicae-plan-test",
            "node:22-bookworm-slim",
            new Dictionary<string, string> { ["CODEX_HOME"] = "/tmp/codex-home" },
            ["/bin/sh", "-lc", "run codex"],
            KubernetesJobAuthMethods.CodexSubscription), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.FailureReason);
        Assert.Contains("agent output", result.Logs);
        Assert.NotNull(api.CreatedJob);
        var container = Assert.Single(api.CreatedJob.Spec.Template.Spec.Containers);
        Assert.Equal("node:22-bookworm-slim", container.Image);
        Assert.Contains(container.Env, env => env.Name == "CODEX_HOME" && env.Value == "/tmp/codex-home");
        Assert.Contains(container.EnvFrom, source => source.SecretRef.Name == "formicae-runtime-secrets");
        Assert.DoesNotContain(container.EnvFrom, source => source.SecretRef.Name == "openhands-llm-api-key");
        Assert.Contains(container.VolumeMounts, mount => mount.Name == "codex-auth" && mount.MountPath == "/root/.codex");
        Assert.Contains(api.CreatedJob.Spec.Template.Spec.Volumes, volume => volume.Secret.SecretName == "formicae-codex-auth");
    }

    [Fact]
    public async Task Kubernetes_runner_maps_failed_job_to_failed_result()
    {
        var api = new CapturingKubernetesJobApi
        {
            Statuses = new Queue<k8s.Models.V1Job>([
                new k8s.Models.V1Job
                {
                    Status = new k8s.Models.V1JobStatus
                    {
                        Conditions =
                        [
                            new k8s.Models.V1JobCondition
                            {
                                Type = "Failed",
                                Status = "True",
                                Reason = "BackoffLimitExceeded",
                                Message = "The agent container failed."
                            }
                        ]
                    }
                }
            ]),
            Pods =
            [
                new k8s.Models.V1Pod
                {
                    Metadata = new k8s.Models.V1ObjectMeta { Name = "formicae-plan-test-pod" }
                }
            ],
            PodLogs = "agent error"
        };
        var runner = new KubernetesJobRunner(api, Options.Create(new KubernetesJobOptions
        {
            Namespace = "formicae",
            PollIntervalSeconds = 1,
            TimeoutSeconds = 5
        }));

        var result = await runner.RunJobAsync(new KubernetesJobSpec(
            "formicae-plan-test",
            "openhands:test",
            new Dictionary<string, string>(),
            ["openhands", "--headless", "--json", "-t", "Plan this"]), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("The agent container failed.", result.FailureReason);
        Assert.Contains("agent error", result.Logs);
    }

    private sealed class CapturingJobRunner : IKubernetesJobRunner
    {
        public KubernetesJobSpec? LastSpec { get; private set; }
        public KubernetesJobResult? Result { get; init; }

        public Task<KubernetesJobResult> RunJobAsync(KubernetesJobSpec spec, CancellationToken cancellationToken)
        {
            LastSpec = spec;
            return Task.FromResult(Result ?? new KubernetesJobResult(true, spec.Name, "ok", null));
        }
    }

    private sealed class CapturingKubernetesJobApi : IKubernetesJobApi
    {
        public k8s.Models.V1Job? CreatedJob { get; private set; }
        public Queue<k8s.Models.V1Job> Statuses { get; init; } = new();
        public IReadOnlyList<k8s.Models.V1Pod> Pods { get; init; } = [];
        public string PodLogs { get; init; } = string.Empty;

        public Task CreateJobAsync(k8s.Models.V1Job job, string namespaceName, CancellationToken cancellationToken)
        {
            CreatedJob = job;
            return Task.CompletedTask;
        }

        public Task<k8s.Models.V1Job> ReadJobStatusAsync(string name, string namespaceName, CancellationToken cancellationToken)
            => Task.FromResult(Statuses.Count == 0
                ? new k8s.Models.V1Job { Status = new k8s.Models.V1JobStatus { Succeeded = 1 } }
                : Statuses.Dequeue());

        public Task<IReadOnlyList<k8s.Models.V1Pod>> ListPodsAsync(string namespaceName, string labelSelector, CancellationToken cancellationToken)
            => Task.FromResult(Pods);

        public Task<string> ReadPodLogAsync(string name, string namespaceName, string container, CancellationToken cancellationToken)
            => Task.FromResult(PodLogs);

        public Task DeleteJobAsync(string name, string namespaceName, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
