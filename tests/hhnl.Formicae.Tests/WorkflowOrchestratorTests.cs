using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.Containers;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Infrastructure.GitHub;
using hhnl.Formicae.Infrastructure.Kubernetes;
using hhnl.Formicae.Infrastructure.OpenHands;
using hhnl.Formicae.Infrastructure.Prompts;
using hhnl.Formicae.Tests.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Octokit;
using Workflow = hhnl.Formicae.Application.Workflows.Workflow;

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
    public async Task DiscoverReadyToPlanWorkflows_Queues_labeled_issues_from_connected_repositories()
    {
        var store = new InMemoryWorkflowStore();
        var integrationStore = new InMemoryDevOpsIntegrationStore();
        var integration = await integrationStore.CreateAsync(new DevOpsIntegration
        {
            ProviderType = DevOpsProviderType.GitHub,
            DisplayName = "GitHub",
            GitHubAppClientId = "client-id",
            CreatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z")
        }, CancellationToken.None);
        await integrationStore.AddRepositoryAsync(new ConnectedRepository
        {
            DevOpsIntegrationId = integration.Id,
            Owner = "acme",
            Name = "widgets",
            RepositoryUrl = "https://github.com/acme/widgets",
            DefaultBranch = "develop",
            CreatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z")
        }, CancellationToken.None);
        await integrationStore.AddRepositoryAsync(new ConnectedRepository
        {
            DevOpsIntegrationId = integration.Id,
            Owner = "acme",
            Name = "tools",
            RepositoryUrl = "https://github.com/acme/tools",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z")
        }, CancellationToken.None);
        var devOps = new MockDevOpsAdapter()
            .AddIssueWithLabels("https://github.com/acme/widgets/issues/42", "Widgets", "Widgets body", [WorkItemWorkflowLabels.ReadyToPlan])
            .AddIssueWithLabels("https://github.com/acme/tools/issues/7", "Tools", "Tools body", [WorkItemWorkflowLabels.ReadyToPlan]);
        var discovery = new WorkflowDiscoveryService(store, devOps, Options.Create(new WorkflowDiscoveryOptions
        {
            Enabled = true
        }), integrationStore: integrationStore);

        var discovered = await discovery.DiscoverReadyToPlanWorkflowsAsync(CancellationToken.None);

        var workflows = await store.ListRunnableWorkflowsAsync(CancellationToken.None);
        Assert.Equal(2, discovered);
        Assert.Contains(workflows, workflow => workflow.RepositoryUrl == "https://github.com/acme/widgets" && workflow.BaseBranch == "develop");
        Assert.Contains(workflows, workflow => workflow.RepositoryUrl == "https://github.com/acme/tools" && workflow.BaseBranch == "main");
        Assert.Contains(devOps.ListIssuesWithLabelCalls, call => call.RepositoryUrl == "https://github.com/acme/widgets");
        Assert.Contains(devOps.ListIssuesWithLabelCalls, call => call.RepositoryUrl == "https://github.com/acme/tools");
    }

    [Fact]
    public async Task DiscoverReadyToPlanWorkflows_continues_when_repository_scan_fails()
    {
        var store = new InMemoryWorkflowStore();
        var integrationStore = new InMemoryDevOpsIntegrationStore();
        var integration = await integrationStore.CreateAsync(new DevOpsIntegration
        {
            ProviderType = DevOpsProviderType.GitHub,
            DisplayName = "GitHub",
            GitHubAppClientId = "client-id",
            CreatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z")
        }, CancellationToken.None);
        await integrationStore.AddRepositoryAsync(new ConnectedRepository
        {
            DevOpsIntegrationId = integration.Id,
            Owner = "acme",
            Name = "broken",
            RepositoryUrl = "https://github.com/acme/broken",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z")
        }, CancellationToken.None);
        await integrationStore.AddRepositoryAsync(new ConnectedRepository
        {
            DevOpsIntegrationId = integration.Id,
            Owner = "acme",
            Name = "tools",
            RepositoryUrl = "https://github.com/acme/tools",
            DefaultBranch = "develop",
            CreatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z")
        }, CancellationToken.None);
        var devOps = new MockDevOpsAdapter()
            .AddIssueWithLabels("https://github.com/acme/tools/issues/7", "Tools", "Tools body", [WorkItemWorkflowLabels.ReadyToPlan]);
        devOps.ListIssuesWithLabelExceptions["https://github.com/acme/broken"] = new InvalidOperationException("Repository unavailable.");
        var discovery = new WorkflowDiscoveryService(store, devOps, Options.Create(new WorkflowDiscoveryOptions
        {
            Enabled = true
        }), integrationStore: integrationStore);

        var discovered = await discovery.DiscoverReadyToPlanWorkflowsAsync(CancellationToken.None);

        var workflow = Assert.Single(await store.ListRunnableWorkflowsAsync(CancellationToken.None));
        Assert.Equal(1, discovered);
        Assert.Equal("https://github.com/acme/tools", workflow.RepositoryUrl);
        Assert.Equal("develop", workflow.BaseBranch);
        Assert.Contains(devOps.ListIssuesWithLabelCalls, call => call.RepositoryUrl == "https://github.com/acme/broken");
        Assert.Contains(devOps.ListIssuesWithLabelCalls, call => call.RepositoryUrl == "https://github.com/acme/tools");
    }

    [Fact]
    public async Task DiscoverReadyToPlanWorkflows_uses_saved_ai_settings_model()
    {
        var store = new InMemoryWorkflowStore();
        var settingsStore = new InMemoryAiSettingsStore();
        var settingsService = new AiSettingsService(
            settingsStore,
            Options.Create(new OpenHandsOptions()),
            new MutableClock(DateTimeOffset.Parse("2026-06-26T12:00:00Z")));
        await settingsService.UpdateAsync(new UpdateAiSettingsRequest(null, "saved-model", null, OpenHandsAuthMethods.ApiKey, null), CancellationToken.None);
        var repositoryUrl = "https://github.com/acme/widgets";
        var issueUrl = "https://github.com/acme/widgets/issues/43";
        var devOps = new MockDevOpsAdapter()
            .AddIssueWithLabels(issueUrl, "Scripted issue", "Scripted issue body", [WorkItemWorkflowLabels.ReadyToPlan]);
        var discovery = new WorkflowDiscoveryService(store, devOps, Options.Create(new WorkflowDiscoveryOptions
        {
            Enabled = true,
            RepositoryUrl = repositoryUrl,
            Model = "discovery-model"
        }), settingsService);

        await discovery.DiscoverReadyToPlanWorkflowsAsync(CancellationToken.None);

        var workflow = Assert.Single(await store.ListRunnableWorkflowsAsync(CancellationToken.None));
        Assert.Equal("saved-model", workflow.Model);
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
    public async Task AdvanceRunnableWorkflows_records_ordered_transition_and_task_events()
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

        var events = await store.ListEventsAsync(started.WorkflowId, CancellationToken.None);
        Assert.Equal(WorkflowEventTypes.WorkflowCompleted, events[0].Type);
        Assert.Contains(events, evt => evt.Type == WorkflowEventTypes.WorkflowTransitioned && evt.Message.Contains("Planning started", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(events, evt => evt.Type == WorkflowEventTypes.TaskStarted && evt.Message.Contains("Plan", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(events, evt => evt.Type == WorkflowEventTypes.TaskSucceeded && evt.Message.Contains("Plan", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(events, evt => evt.Type == WorkflowEventTypes.PullRequestCreated);
        Assert.Equal(WorkflowEventTypes.WorkflowQueued, events[^1].Type);
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
    public async Task AdvanceRunnableWorkflows_Schedules_multiple_agent_jobs_without_waiting_for_completion()
    {
        var store = new InMemoryWorkflowStore();
        var service = new WorkflowService(store);
        var repositoryUrl = "https://github.com/acme/widgets";
        var firstIssueUrl = "https://github.com/acme/widgets/issues/42";
        var secondIssueUrl = "https://github.com/acme/widgets/issues/43";
        var devOps = new MockDevOpsAdapter()
            .AddIssueWithLabels(firstIssueUrl, "First issue", "First body", [WorkItemWorkflowLabels.ReadyToPlan])
            .AddIssueWithLabels(secondIssueUrl, "Second issue", "Second body", [WorkItemWorkflowLabels.ReadyToPlan]);
        var first = await service.StartGitHubIssueWorkflowAsync(new StartGitHubIssueWorkflowRequest(firstIssueUrl, repositoryUrl, null, null), CancellationToken.None);
        var second = await service.StartGitHubIssueWorkflowAsync(new StartGitHubIssueWorkflowRequest(secondIssueUrl, repositoryUrl, null, null), CancellationToken.None);
        var agentRunner = new DeferredAgentRunner();
        var orchestrator = new WorkflowOrchestrator(store, devOps, devOps, agentRunner, new FilePromptRenderer());

        var advanced = await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        var firstWorkflow = await store.GetWorkflowAsync(first.WorkflowId, CancellationToken.None);
        var secondWorkflow = await store.GetWorkflowAsync(second.WorkflowId, CancellationToken.None);
        var firstRun = await store.GetTaskRunAsync(first.WorkflowId, TaskRunKind.Plan, CancellationToken.None);
        var secondRun = await store.GetTaskRunAsync(second.WorkflowId, TaskRunKind.Plan, CancellationToken.None);

        Assert.Equal(2, advanced);
        Assert.Equal(2, agentRunner.StartedTasks.Count);
        Assert.NotNull(firstWorkflow);
        Assert.NotNull(secondWorkflow);
        Assert.Equal(WorkflowStatus.Planning, firstWorkflow.Status);
        Assert.Equal(WorkflowStatus.Planning, secondWorkflow.Status);
        Assert.NotNull(firstRun);
        Assert.NotNull(secondRun);
        Assert.Equal(TaskRunStatus.Running, firstRun.Status);
        Assert.Equal(TaskRunStatus.Running, secondRun.Status);
        Assert.Equal(agentRunner.StartedExternalIds[0], firstRun.ExternalId);
        Assert.Equal(agentRunner.StartedExternalIds[1], secondRun.ExternalId);
    }

    [Fact]
    public async Task AdvanceRunnableWorkflows_preserves_started_at_for_deferred_run_and_sets_completed_at_later()
    {
        var store = new InMemoryWorkflowStore();
        var issueUrl = "https://github.com/acme/widgets/issues/44";
        var devOps = new MockDevOpsAdapter()
            .AddIssueWithLabels(issueUrl, "Scripted issue", "Scripted issue body", [WorkItemWorkflowLabels.ReadyToPlan]);
        var workflow = await store.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = issueUrl,
            RepositoryUrl = "https://github.com/acme/widgets"
        }, CancellationToken.None);
        var clock = new MutableClock(DateTimeOffset.Parse("2026-06-26T12:00:00Z"));
        var agentRunner = new DeferredAgentRunner();
        var orchestrator = new WorkflowOrchestrator(store, devOps, devOps, agentRunner, new FilePromptRenderer(), clock);

        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        var run = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.Plan, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal(clock.UtcNow, run.StartedAt);
        Assert.Null(run.CompletedAt);

        clock.UtcNow = DateTimeOffset.Parse("2026-06-26T12:10:00Z");
        agentRunner.Results[run.ExternalId!] = new AgentRunResult(true, run.ExternalId!, "Deferred plan output", null);
        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        run = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.Plan, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal(DateTimeOffset.Parse("2026-06-26T12:00:00Z"), run.StartedAt);
        Assert.Equal(clock.UtcNow, run.CompletedAt);
    }

    [Fact]
    public async Task AdvanceRunnableWorkflows_Continues_implementation_when_issue_reaction_fails()
    {
        var store = new InMemoryWorkflowStore();
        var issueUrl = "https://github.com/acme/widgets/issues/43";
        var workflow = await store.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = issueUrl,
            RepositoryUrl = "https://github.com/acme/widgets",
            Status = WorkflowStatus.Implementing,
            CurrentStep = WorkflowStep.Implement
        }, CancellationToken.None);
        await store.UpsertTaskRunAsync(new TaskRun
        {
            WorkflowId = workflow.Id,
            Kind = TaskRunKind.Plan,
            Status = TaskRunStatus.Succeeded,
            Output = "Plan output"
        }, CancellationToken.None);
        var devOps = new MockDevOpsAdapter
        {
            ReactToIssueException = new InvalidOperationException("Resource not accessible by integration")
        };
        var orchestrator = new WorkflowOrchestrator(store, devOps, devOps, new FakeAgentRunner(), new FilePromptRenderer());

        var advanced = await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        var updated = await store.GetWorkflowAsync(workflow.Id, CancellationToken.None);
        var implementRun = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.Implement, CancellationToken.None);
        var logs = await store.ListLogsAsync(workflow.Id, CancellationToken.None);

        Assert.Equal(1, advanced);
        Assert.NotNull(updated);
        Assert.Equal(WorkflowStatus.CreatingPullRequest, updated.Status);
        Assert.Equal(WorkflowStep.CreatePullRequest, updated.CurrentStep);
        Assert.NotNull(implementRun);
        Assert.Equal(TaskRunStatus.Succeeded, implementRun.Status);
        Assert.Single(devOps.CreateBranchCalls);
        Assert.Contains(logs, log => log.Level == "Warning" && log.Message.Contains("GitHub reaction feedback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AdvanceRunnableWorkflows_records_exception_stack_trace_when_implementation_fails_before_task_run()
    {
        var store = new InMemoryWorkflowStore();
        var issueUrl = "https://github.com/acme/widgets/issues/45";
        var workflow = await store.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = issueUrl,
            RepositoryUrl = "https://github.com/acme/widgets",
            Status = WorkflowStatus.Implementing,
            CurrentStep = WorkflowStep.Implement
        }, CancellationToken.None);
        await store.UpsertTaskRunAsync(new TaskRun
        {
            WorkflowId = workflow.Id,
            Kind = TaskRunKind.Plan,
            Status = TaskRunStatus.Succeeded,
            Output = "Plan output"
        }, CancellationToken.None);
        var devOps = new MockDevOpsAdapter
        {
            CreateBranchException = new NullReferenceException("GraphQL linked branch failed.")
        };
        var orchestrator = new WorkflowOrchestrator(store, devOps, devOps, new FakeAgentRunner(), new FilePromptRenderer());

        var advanced = await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        var updated = await store.GetWorkflowAsync(workflow.Id, CancellationToken.None);
        var events = await store.ListEventsAsync(workflow.Id, CancellationToken.None);
        var logs = await store.ListLogsAsync(workflow.Id, CancellationToken.None);
        var implementRun = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.Implement, CancellationToken.None);

        Assert.Equal(1, advanced);
        Assert.NotNull(updated);
        Assert.Equal(WorkflowStatus.Failed, updated.Status);
        Assert.Equal("GraphQL linked branch failed.", updated.FailureReason);
        Assert.Null(implementRun);
        var failed = Assert.Single(events, evt => evt.Type == WorkflowEventTypes.WorkflowFailed);
        Assert.Contains("System.NullReferenceException", failed.DetailsJson);
        Assert.Contains("GraphQL linked branch failed.", failed.DetailsJson);
        Assert.Contains("stackTrace", failed.DetailsJson);
        Assert.Contains(logs, log => log.Level == "Error" && log.Message.Contains("System.NullReferenceException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdvanceRunnableWorkflows_keeps_workflow_runnable_when_work_item_provider_is_temporarily_unavailable()
    {
        var store = new InMemoryWorkflowStore();
        var clock = new MutableClock(DateTimeOffset.Parse("2026-06-26T12:00:00Z"));
        var issueUrl = "https://github.com/acme/widgets/issues/44";
        var workflow = await store.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = issueUrl,
            RepositoryUrl = "https://github.com/acme/widgets",
            Status = WorkflowStatus.Implementing,
            CurrentStep = WorkflowStep.Implement,
            UpdatedAt = clock.UtcNow.AddMinutes(-5)
        }, CancellationToken.None);
        await store.UpsertTaskRunAsync(new TaskRun
        {
            WorkflowId = workflow.Id,
            Kind = TaskRunKind.Plan,
            Status = TaskRunStatus.Succeeded,
            Output = "Plan output",
            UpdatedAt = clock.UtcNow.AddMinutes(-4)
        }, CancellationToken.None);
        var devOps = new MockDevOpsAdapter
        {
            GetIssueException = new WorkItemProviderUnavailableException("GitHub rate limit exceeded.")
        };
        var orchestrator = new WorkflowOrchestrator(store, devOps, devOps, new FakeAgentRunner(), new FilePromptRenderer(), clock);

        var advanced = await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        var updated = await store.GetWorkflowAsync(workflow.Id, CancellationToken.None);
        var implementRun = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.Implement, CancellationToken.None);
        var logs = await store.ListLogsAsync(workflow.Id, CancellationToken.None);

        Assert.Equal(0, advanced);
        Assert.NotNull(updated);
        Assert.Equal(WorkflowStatus.Implementing, updated.Status);
        Assert.Equal(WorkflowStep.Implement, updated.CurrentStep);
        Assert.Null(updated.FailureReason);
        Assert.Null(implementRun);
        Assert.Empty(devOps.CreateBranchCalls);
        Assert.Contains(logs, log => log.Level == "Warning" && log.Message.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase));
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
        Assert.Empty(devOps.AddIssueCommentCalls);

        devOps.AddIssueWithLabels(
            issueUrl,
            "Scripted issue",
            "Scripted issue body",
            [WorkItemWorkflowLabels.ReadyToPlan, WorkItemWorkflowLabels.ReadyToImplement]);

        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        runs = await store.ListTaskRunsAsync(started.WorkflowId, CancellationToken.None);

        Assert.Contains(runs, run => run.Kind == TaskRunKind.Implement && run.Status == TaskRunStatus.Succeeded);
        Assert.Single(devOps.CreateBranchCalls);
        Assert.Collection(devOps.ReactToIssueCalls,
            call => Assert.Equal(WorkflowReactionContent.PlanningStarted, call.Reaction),
            call =>
            {
                Assert.Equal(issueUrl, call.IssueUrl);
                Assert.Equal(WorkflowReactionContent.ImplementationStarted, call.Reaction);
            });
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
    public async Task AdvanceRunnableWorkflows_Waits_for_running_agent_task_without_external_id()
    {
        var store = new InMemoryWorkflowStore();
        var issueUrl = "https://github.com/acme/widgets/issues/42";
        var workflow = await store.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = issueUrl,
            RepositoryUrl = "https://github.com/acme/widgets",
            Status = WorkflowStatus.Implementing,
            CurrentStep = WorkflowStep.Implement
        }, CancellationToken.None);
        await store.UpsertTaskRunAsync(new TaskRun
        {
            WorkflowId = workflow.Id,
            Kind = TaskRunKind.Implement,
            Status = TaskRunStatus.Running
        }, CancellationToken.None);
        var devOps = new MockDevOpsAdapter()
            .AddIssueWithLabels(issueUrl, "Scripted issue", "Scripted issue body", [WorkItemWorkflowLabels.ReadyToImplement]);
        var orchestrator = new WorkflowOrchestrator(store, devOps, devOps, new DeferredAgentRunner(), new FilePromptRenderer());

        var advanced = await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        var updated = await store.GetWorkflowAsync(workflow.Id, CancellationToken.None);
        var run = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.Implement, CancellationToken.None);
        Assert.Equal(0, advanced);
        Assert.NotNull(updated);
        Assert.Equal(WorkflowStatus.Implementing, updated.Status);
        Assert.Null(updated.FailureReason);
        Assert.NotNull(run);
        Assert.Equal(TaskRunStatus.Running, run.Status);
        Assert.Null(run.ExternalId);
    }

    [Fact]
    public async Task AdvanceRunnableWorkflows_Does_not_add_revision_comment_for_deferred_first_plan_with_streamed_output()
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
        var agentRunner = new DeferredAgentRunner();
        var orchestrator = new WorkflowOrchestrator(store, devOps, devOps, agentRunner, new FilePromptRenderer());

        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        var run = await store.GetTaskRunAsync(started.WorkflowId, TaskRunKind.Plan, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal(TaskRunStatus.Running, run.Status);
        Assert.NotNull(run.ExternalId);

        var messageService = new WorkerAgentMessageService(store);
        await messageService.RecordAsync(new WorkerAgentMessageRequest(
            started.WorkflowId,
            "Plan",
            run.ExternalId,
            "stdout",
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Partial first plan output\"}}",
            DateTimeOffset.Parse("2026-06-26T12:00:00Z")), CancellationToken.None);

        agentRunner.Results[run.ExternalId] = new AgentRunResult(true, run.ExternalId, "Deferred plan output", null);
        await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        Assert.Collection(devOps.UpsertIssueCommentCalls, call =>
        {
            Assert.Equal(issueUrl, call.IssueUrl);
            Assert.Equal(PullRequestCommentMarkers.Plan(started.WorkflowId), call.Marker);
            Assert.Contains("Deferred plan output", call.Body);
        });
        Assert.Empty(devOps.AddIssueCommentCalls);
    }

    [Fact]
    public async Task AdvanceRunnableWorkflows_Revises_plan_when_issue_comment_is_newer_than_plan()
    {
        var store = new InMemoryWorkflowStore();
        var issueUrl = "https://github.com/acme/widgets/issues/42";
        var previousPlanUpdatedAt = DateTimeOffset.Parse("2026-06-25T10:00:00Z");
        var devOps = new MockDevOpsAdapter()
            .AddIssueWithLabels(
                issueUrl,
                "Scripted issue",
                "Scripted issue body",
                [WorkItemWorkflowLabels.ReadyToPlan, WorkItemWorkflowLabels.ReadyToImplement])
            .AddIssueComment(issueUrl, "new-feedback", "maintainer", "Please add the webhook retry behavior to the plan.", previousPlanUpdatedAt.AddMinutes(5))
            .AddIssueComment(issueUrl, "automation", "formicae", PullRequestCommentMarkers.PlanRevisionSummary(Guid.NewGuid()) + " automated summary", previousPlanUpdatedAt.AddMinutes(10));
        var workflow = await store.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = issueUrl,
            RepositoryUrl = "https://github.com/acme/widgets",
            Status = WorkflowStatus.Implementing,
            CurrentStep = WorkflowStep.Implement,
            PlanArtifact = "Existing plan output"
        }, CancellationToken.None);
        await store.UpsertTaskRunAsync(new TaskRun
        {
            WorkflowId = workflow.Id,
            Kind = TaskRunKind.Plan,
            Status = TaskRunStatus.Succeeded,
            ExternalId = "existing-plan",
            Output = "Existing plan output",
            UpdatedAt = previousPlanUpdatedAt
        }, CancellationToken.None);
        var agentRunner = new CapturingAgentRunner();
        var orchestrator = new WorkflowOrchestrator(store, devOps, devOps, agentRunner, new FilePromptRenderer());

        var advanced = await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        var updated = await store.GetWorkflowAsync(workflow.Id, CancellationToken.None);
        var planRun = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.Plan, CancellationToken.None);
        Assert.Equal(1, advanced);
        Assert.NotNull(updated);
        Assert.Equal(WorkflowStatus.Implementing, updated.Status);
        Assert.Equal(WorkflowStep.Implement, updated.CurrentStep);
        Assert.NotNull(planRun);
        Assert.Equal(TaskRunStatus.Succeeded, planRun.Status);
        Assert.Contains("Captured Plan output", planRun.Output);
        Assert.Empty(devOps.CreateBranchCalls);
        Assert.NotNull(agentRunner.LastTask);
        Assert.Contains("Existing plan output", agentRunner.LastTask.Prompt);
        Assert.Contains("Please add the webhook retry behavior to the plan.", agentRunner.LastTask.Prompt);
        Assert.DoesNotContain("automated summary", agentRunner.LastTask.Prompt);
        Assert.Collection(devOps.ReactToIssueCommentCalls, call =>
        {
            Assert.Equal(issueUrl, call.IssueUrl);
            Assert.Equal("new-feedback", call.CommentId);
            Assert.Equal(WorkflowReactionContent.FeedbackStarted, call.Reaction);
        });
        Assert.Collection(devOps.UpsertIssueCommentCalls, call =>
        {
            Assert.Equal(issueUrl, call.IssueUrl);
            Assert.Equal(PullRequestCommentMarkers.Plan(workflow.Id), call.Marker);
            Assert.Contains("Captured Plan output", call.Body);
        });
        Assert.Collection(devOps.AddIssueCommentCalls, call =>
        {
            Assert.Equal(issueUrl, call.IssueUrl);
            Assert.Contains(PullRequestCommentMarkers.PlanRevisionSummary(workflow.Id), call.Body);
            Assert.Contains("updated the implementation plan", call.Body);
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
        Assert.Equal($"formicae/{started.WorkflowId:N}", workflow.BranchName);
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
            Assert.Equal(repositoryUrl, call.Request.RepositoryUrl);
            Assert.Equal("develop", call.Request.BaseBranch);
            Assert.Equal($"formicae/{started.WorkflowId:N}", call.Request.BranchName);
            Assert.Equal(issueUrl, call.Request.LinkedWorkItemUrl);
        });
        Assert.Collection(devOps.CreatePullRequestCalls, call =>
        {
            Assert.Equal(started.WorkflowId, call.WorkflowId);
            Assert.Equal(repositoryUrl, call.RepositoryUrl);
            Assert.Equal($"formicae/{started.WorkflowId:N}", call.BranchName);
            Assert.Contains(call.TaskRuns, run => run.Kind == TaskRunKind.Plan);
            Assert.Contains(call.TaskRuns, run => run.Kind == TaskRunKind.Implement);
        });
        Assert.Collection(devOps.ReactToIssueCalls,
            call =>
            {
                Assert.Equal(issueUrl, call.IssueUrl);
                Assert.Equal(WorkflowReactionContent.PlanningStarted, call.Reaction);
            },
            call =>
            {
                Assert.Equal(issueUrl, call.IssueUrl);
                Assert.Equal(WorkflowReactionContent.ImplementationStarted, call.Reaction);
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
            Assert.Equal(WorkflowReactionContent.PullRequestCommentStarted, call.Reaction);
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
    public async Task AdvanceRunnableWorkflows_Completes_reviewing_workflow_when_pull_request_is_merged()
    {
        var store = new InMemoryWorkflowStore();
        var devOps = new MockDevOpsAdapter
        {
            DefaultPullRequestUrl = "https://github.com/acme/widgets/pull/29",
            DefaultPullRequestStatus = new PullRequestStatus(false, true)
        };
        var workflow = await store.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = "https://github.com/acme/widgets/issues/6",
            RepositoryUrl = "https://github.com/acme/widgets",
            BranchName = "formicae/merged-pr",
            PullRequestUrl = devOps.DefaultPullRequestUrl,
            Status = WorkflowStatus.Reviewing,
            CurrentStep = WorkflowStep.AddressComments
        }, CancellationToken.None);
        var orchestrator = new WorkflowOrchestrator(store, devOps, devOps, new FakeAgentRunner(), new FilePromptRenderer());

        var advanced = await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        var updated = await store.GetWorkflowAsync(workflow.Id, CancellationToken.None);
        Assert.Equal(1, advanced);
        Assert.NotNull(updated);
        Assert.Equal(WorkflowStatus.Completed, updated.Status);
        Assert.Equal(WorkflowStep.Done, updated.CurrentStep);
        Assert.Empty(devOps.ListPullRequestCommentsCalls);
        Assert.Collection(devOps.GetPullRequestStatusCalls, call =>
        {
            Assert.Equal(workflow.Id, call.WorkflowId);
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
        var agentRunner = new CapturingAgentRunner();
        var orchestrator = new WorkflowOrchestrator(store, devOps, devOps, agentRunner, new FilePromptRenderer());

        var advanced = await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);

        var updated = await store.GetWorkflowAsync(workflow.Id, CancellationToken.None);
        var run = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.AddressComments, CancellationToken.None);
        Assert.Equal(1, advanced);
        Assert.NotNull(updated);
        Assert.Equal(WorkflowStatus.Completed, updated.Status);
        Assert.Equal(WorkflowStep.Done, updated.CurrentStep);
        Assert.NotNull(run);
        Assert.Equal(TaskRunStatus.Succeeded, run.Status);
        Assert.Contains("Captured AddressComments output", run.Output);
        Assert.Collection(devOps.ReactToPullRequestCommentCalls, call =>
        {
            Assert.Equal(workflow.Id, call.WorkflowId);
            Assert.Equal("new", call.CommentId);
            Assert.Equal(WorkflowReactionContent.PullRequestCommentStarted, call.Reaction);
        });
        Assert.NotNull(agentRunner.LastTask);
        Assert.Contains("Comments to address:", agentRunner.LastTask.Prompt);
        Assert.Contains("Anything else to add?", agentRunner.LastTask.Prompt);
        Assert.DoesNotContain("Already addressed.", agentRunner.LastTask.Prompt);
        Assert.Contains("/workspace/formicae/context/pull-request-conversation.md", agentRunner.LastTask.Prompt);
        var contextFile = Assert.Single(agentRunner.LastTask.ContextFiles!);
        Assert.Equal("pull-request-conversation.md", contextFile.FileName);
        Assert.Contains(devOps.DefaultPullRequestUrl, contextFile.Content);
        Assert.Contains("Already addressed.", contextFile.Content);
        Assert.Contains("Anything else to add?", contextFile.Content);
        Assert.Collection(devOps.UpsertPullRequestCommentCalls, call =>
        {
            Assert.Equal(workflow.Id, call.WorkflowId);
            Assert.Contains("Captured AddressComments output", call.Body);
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
        var events = await store.ListEventsAsync(started.WorkflowId, CancellationToken.None);

        Assert.NotNull(workflow);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status);
        Assert.Equal("address comments failed", workflow.FailureReason);
        Assert.Contains(runs, run => run.Kind == TaskRunKind.AddressComments && run.Status == TaskRunStatus.Failed);
        var taskFailed = Assert.Single(events, evt => evt.Type == WorkflowEventTypes.TaskFailed);
        var detailsJson = taskFailed.DetailsJson ?? string.Empty;
        Assert.Contains("\"taskKind\":\"AddressComments\"", detailsJson);
        Assert.Contains("\"externalId\":\"address-comments-run\"", detailsJson);
        Assert.Contains("\"failureReason\":\"address comments failed\"", detailsJson);
        Assert.Contains("\"outputExcerpt\":\"failed\"", detailsJson);
        Assert.Contains(events, evt => evt.Type == WorkflowEventTypes.WorkflowFailed && evt.Message == "address comments failed");
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
        Assert.Contains("Comments to address:", prompt);
        Assert.Contains("/workspace/formicae/context/pull-request-conversation.md", prompt);
        Assert.Contains("Please cover this edge case.", prompt);
    }

    [Fact]
    public void OctokitGitHubApi_GraphQl_issue_response_deserializes_with_octokit_simple_json()
    {
        const string json = """
            {
              "data": {
                "repository": {
                  "issue": {
                    "id": "I_kwDOIssueNode"
                  }
                }
              }
            }
            """;

        var response = DeserializeWithOctokitSimpleJson<OctokitGitHubApi.IssueNodeIdGraphQlResponse>(json);

        Assert.Equal("I_kwDOIssueNode", response.data?.repository?.issue?.id);
    }

    [Fact]
    public void OctokitGitHubApi_GraphQl_variable_serialization_preserves_issueId_key()
    {
        var body = new Dictionary<string, object?>
        {
            ["query"] = "mutation($issueId: ID!) { noop }",
            ["variables"] = new Dictionary<string, object?>
            {
                ["issueId"] = "I_kwDOTEB3Fc8AAAABG9ZiRw",
                ["oid"] = "base-sha",
                ["name"] = "formicae/test"
            }
        };

        var json = SerializeWithOctokitSimpleJson(body);

        Assert.Contains("\"issueId\":\"I_kwDOTEB3Fc8AAAABG9ZiRw\"", json);
        Assert.DoesNotContain("issue_id", json);
    }

    [Fact]
    public void OctokitGitHubApi_GraphQl_linked_branch_response_deserializes_with_octokit_simple_json()
    {
        const string json = """
            {
              "data": {
                "createLinkedBranch": {
                  "linkedBranch": {
                    "ref": {
                      "name": "formicae/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                    }
                  }
                }
              }
            }
            """;

        var response = DeserializeWithOctokitSimpleJson<OctokitGitHubApi.CreateLinkedBranchGraphQlResponse>(json);

        Assert.Equal("formicae/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", response.data?.createLinkedBranch?.linkedBranch?.@ref?.name);
    }

    private static string SerializeWithOctokitSimpleJson(object value)
    {
        var simpleJsonType = typeof(GitHubClient).Assembly.GetType("Octokit.SimpleJson")
            ?? throw new InvalidOperationException("Octokit.SimpleJson type was not found.");
        var serializeMethod = simpleJsonType
            .GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Single(method => method.Name == "SerializeObject"
                && method.GetParameters() is [{ ParameterType: { } parameterType }]
                && parameterType == typeof(object));

        return (string)serializeMethod.Invoke(null, [value])!;
    }

    private static T DeserializeWithOctokitSimpleJson<T>(string json)
    {
        var simpleJsonType = typeof(GitHubClient).Assembly.GetType("Octokit.SimpleJson")
            ?? throw new InvalidOperationException("Octokit.SimpleJson type was not found.");
        var deserializeMethod = simpleJsonType
            .GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Single(method => method.Name == "DeserializeObject"
                && method.IsGenericMethodDefinition
                && method.GetParameters() is [{ ParameterType: { } parameterType }]
                && parameterType == typeof(string));

        return (T)deserializeMethod.MakeGenericMethod(typeof(T)).Invoke(null, [json])!;
    }
    [Fact]
    public async Task GitHubSourceControlProvider_CreateBranchAsync_uses_linked_branch_mutation_inputs()
    {
        var api = new CapturingGitHubApi();
        var provider = new GitHubSourceControlProvider(api);

        var branch = await provider.CreateBranchAsync(new CreateBranchRequest(
            "https://github.com/acme/widgets",
            "main",
            "formicae/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "https://github.com/acme/widgets/issues/42"),
            CancellationToken.None);

        Assert.Equal("formicae/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", branch);
        Assert.Equal("heads/main", api.ReferenceName);
        Assert.Collection(api.LinkedBranchCalls, call =>
        {
            Assert.Equal("acme", call.Owner);
            Assert.Equal("widgets", call.Repository);
            Assert.Equal(42, call.IssueNumber);
            Assert.Equal("base-sha", call.BaseOid);
            Assert.Equal("formicae/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", call.BranchName);
        });
    }

    [Fact]
    public async Task GitHubSourceControlProvider_CreateBranchAsync_is_idempotent_when_linked_branch_already_exists()
    {
        var api = new CapturingGitHubApi { ThrowLinkedBranchAlreadyExists = true };
        var provider = new GitHubSourceControlProvider(api);

        var branch = await provider.CreateBranchAsync(new CreateBranchRequest(
            "https://github.com/acme/widgets",
            "main",
            "formicae/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "https://github.com/acme/widgets/issues/42"),
            CancellationToken.None);

        Assert.Equal("formicae/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", branch);
        Assert.Single(api.LinkedBranchCalls);
    }

    [Fact]
    public async Task GitHubSourceControlProvider_CreateBranchAsync_does_not_create_unlinked_ref_when_linked_branch_fails()
    {
        var api = new CapturingGitHubApi { ThrowLinkedBranchUnexpectedly = true };
        var provider = new GitHubSourceControlProvider(api);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => provider.CreateBranchAsync(new CreateBranchRequest(
            "https://github.com/acme/widgets",
            "main",
            "formicae/cccccccccccccccccccccccccccccccc",
            "https://github.com/acme/widgets/issues/42"),
            CancellationToken.None));

        Assert.IsNotType<OperationCanceledException>(exception);
        Assert.Single(api.LinkedBranchCalls);
        Assert.Empty(api.CreatedReferences);
    }

    [Fact]
    public async Task GitHubSourceControlProvider_CreateBranchAsync_rejects_linked_work_item_from_another_repository()
    {
        var api = new CapturingGitHubApi();
        var provider = new GitHubSourceControlProvider(api);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.CreateBranchAsync(new CreateBranchRequest(
            "https://github.com/acme/widgets",
            "main",
            "formicae/dddddddddddddddddddddddddddddddd",
            "https://github.com/acme/other/issues/42"),
            CancellationToken.None));

        Assert.Empty(api.LinkedBranchCalls);
    }

    [Fact]
    public async Task GitHubSourceControlProvider_Adds_implementation_summary_to_created_pull_request_body()
    {
        var api = new CapturingGitHubApi();
        var provider = new GitHubSourceControlProvider(api);
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

        var result = await provider.CreatePullRequestAsync(workflow, runs, CancellationToken.None);

        Assert.Equal("https://github.com/acme/widgets/pull/123", result.Url);
        Assert.NotNull(api.CreatedPullRequest);
        Assert.Equal(false, api.CreatedPullRequest.Draft);
        Assert.Equal("Issue title", api.CreatedPullRequest.Title);
        Assert.Contains("## Implementation Summary", api.CreatedPullRequest.Body);
        Assert.Contains("Implemented the management UI and recent workflow API.", api.CreatedPullRequest.Body);
        Assert.Collection(api.CreatedFiles, file => Assert.Equal(".formicae/workflows/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.md", file.Path));
    }

    [Fact]
    public async Task GitHubWorkItemProvider_Reacts_to_issue()
    {
        var api = new CapturingGitHubApi();
        var provider = new GitHubWorkItemProvider(api);

        await provider.ReactToIssueAsync(
            "https://github.com/acme/widgets/issues/42",
            WorkflowReactionContent.PlanningStarted,
            CancellationToken.None);

        Assert.Equal([new ReactionCall("issue", "acme", "widgets", 42, WorkflowReactionContent.PlanningStarted)], api.ReactionCalls);
    }

    [Fact]
    public async Task GitHubWorkItemProvider_Reacts_to_issue_comment()
    {
        var api = new CapturingGitHubApi();
        var provider = new GitHubWorkItemProvider(api);

        await provider.ReactToIssueCommentAsync(
            "https://github.com/acme/widgets/issues/42",
            new WorkItemComment("123", "maintainer", "Please update the plan.", "https://github.com/acme/widgets/issues/42#issuecomment-123", DateTimeOffset.UtcNow),
            WorkflowReactionContent.FeedbackStarted,
            CancellationToken.None);

        Assert.Equal([new ReactionCall("issue-comment", "acme", "widgets", 123, WorkflowReactionContent.FeedbackStarted)], api.ReactionCalls);
    }

    [Fact]
    public async Task GitHubSourceControlProvider_Lists_issue_and_review_comments_for_pull_request()
    {
        var api = new CapturingGitHubApi();
        api.IssueComments.Add(Model<IssueComment>(
            (nameof(IssueComment.Id), 10L),
            (nameof(IssueComment.Body), "Please update the docs."),
            (nameof(IssueComment.HtmlUrl), "https://github.com/acme/widgets/pull/123#issuecomment-10"),
            (nameof(IssueComment.UpdatedAt), DateTimeOffset.Parse("2026-06-25T10:00:00Z")),
            (nameof(IssueComment.CreatedAt), DateTimeOffset.Parse("2026-06-25T09:59:00Z")),
            (nameof(IssueComment.User), GitHubUser("maintainer"))));
        api.IssueComments.Add(Model<IssueComment>((nameof(IssueComment.Id), 11L), (nameof(IssueComment.Body), "   ")));
        api.IssueComments.Add(Model<IssueComment>((nameof(IssueComment.Id), 12L), (nameof(IssueComment.Body), "<!-- formicae:workflow:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:address-comments --> automated summary")));
        api.ReviewComments.Add(Model<PullRequestReviewComment>(
            (nameof(PullRequestReviewComment.Id), 20L),
            (nameof(PullRequestReviewComment.Body), "Please add a regression test."),
            (nameof(PullRequestReviewComment.HtmlUrl), "https://github.com/acme/widgets/pull/123#discussion_r20"),
            (nameof(PullRequestReviewComment.UpdatedAt), DateTimeOffset.Parse("2026-06-25T10:02:00Z")),
            (nameof(PullRequestReviewComment.User), GitHubUser("reviewer"))));
        var provider = new GitHubSourceControlProvider(api);
        var workflow = new Workflow
        {
            RepositoryUrl = "https://github.com/acme/widgets",
            IssueUrl = "https://github.com/acme/widgets/issues/42",
            PullRequestUrl = "https://github.com/acme/widgets/pull/123"
        };

        var comments = await provider.ListPullRequestCommentsAsync(workflow, CancellationToken.None);

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

    [Fact]
    public async Task GitHubSourceControlProvider_Posts_marked_pull_request_comment()
    {
        var api = new CapturingGitHubApi();
        var provider = new GitHubSourceControlProvider(api);
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

        Assert.Collection(api.CreatedIssueComments, comment =>
        {
            Assert.Equal("acme", comment.Owner);
            Assert.Equal("widgets", comment.Repository);
            Assert.Equal(123, comment.Number);
            Assert.Contains(PullRequestCommentMarkers.AddressComments(workflow.Id), comment.Body);
            Assert.Contains("Addressed the requested changes.", comment.Body);
        });
    }

    [Fact]
    public async Task GitHubSourceControlProvider_Reacts_to_issue_and_review_comments()
    {
        var api = new CapturingGitHubApi();
        var provider = new GitHubSourceControlProvider(api);
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
            WorkflowReactionContent.PullRequestCommentStarted,
            CancellationToken.None);
        await provider.ReactToPullRequestCommentAsync(
            workflow,
            new PullRequestComment("review:20", "reviewer", "Please add a test.", "https://github.com/acme/widgets/pull/123#discussion_r20", DateTimeOffset.UtcNow, PullRequestCommentKind.ReviewComment),
            WorkflowReactionContent.PullRequestCommentStarted,
            CancellationToken.None);

        Assert.Equal([
            new ReactionCall("issue-comment", "acme", "widgets", 10, WorkflowReactionContent.PullRequestCommentStarted),
            new ReactionCall("review-comment", "acme", "widgets", 20, WorkflowReactionContent.PullRequestCommentStarted)
        ], api.ReactionCalls);
    }

    private sealed class FailingAddressCommentsAgentRunner : IAgentRunner
    {
        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken cancellationToken)
        {
            var result = task.Kind == TaskRunKind.AddressComments
                ? new AgentRunResult(false, "address-comments-run", "failed", "address comments failed")
                : new AgentRunResult(true, $"fake-{task.Kind.ToString().ToLowerInvariant()}", $"Fake {task.Kind} output.", null);
            return Task.FromResult(new AgentRunStartResult(result.ExternalId, result));
        }

        public Task<AgentRunResult?> TryGetResultAsync(string externalId, CancellationToken cancellationToken)
            => Task.FromResult<AgentRunResult?>(null);
    }

    private sealed class CapturingGitHubApi : IGitHubApi
    {
        public string? ReferenceName { get; private set; }
        public bool ThrowLinkedBranchAlreadyExists { get; init; }
        public bool ThrowLinkedBranchUnexpectedly { get; init; }
        public NewPullRequest? CreatedPullRequest { get; private set; }
        public List<IssueComment> IssueComments { get; } = [];
        public List<PullRequestReviewComment> ReviewComments { get; } = [];
        public List<LinkedBranchCall> LinkedBranchCalls { get; } = [];
        public List<CreatedReference> CreatedReferences { get; } = [];
        public List<CreatedIssueComment> CreatedIssueComments { get; } = [];
        public List<CreatedFile> CreatedFiles { get; } = [];
        public List<ReactionCall> ReactionCalls { get; } = [];

        public Task<Issue> GetIssueAsync(string owner, string repository, int number)
            => Task.FromResult(Model<Issue>(
                (nameof(Issue.HtmlUrl), $"https://github.com/{owner}/{repository}/issues/{number}"),
                (nameof(Issue.Title), "Issue title"),
                (nameof(Issue.Body), "Issue body"),
                (nameof(Issue.Labels), Array.Empty<Label>())));

        public Task<IReadOnlyList<Issue>> ListIssuesWithLabelAsync(string owner, string repository, string label)
            => Task.FromResult<IReadOnlyList<Issue>>([]);

        public Task<IReadOnlyList<IssueComment>> GetIssueCommentsAsync(string owner, string repository, int number)
            => Task.FromResult<IReadOnlyList<IssueComment>>(IssueComments);

        public Task CreateIssueCommentAsync(string owner, string repository, int number, string body)
        {
            CreatedIssueComments.Add(new CreatedIssueComment(owner, repository, number, body));
            return Task.CompletedTask;
        }

        public Task UpdateIssueCommentAsync(string owner, string repository, long commentId, string body)
            => Task.CompletedTask;

        public Task ReactToIssueAsync(string owner, string repository, int number, string reaction)
        {
            ReactionCalls.Add(new ReactionCall("issue", owner, repository, number, reaction));
            return Task.CompletedTask;
        }

        public Task<Reference> GetReferenceAsync(string owner, string repository, string reference)
        {
            ReferenceName = reference;
            return Task.FromResult(Model<Reference>((nameof(Reference.Object), Model<TagObject>((nameof(TagObject.Sha), "base-sha")))));
        }

        public Task<string> CreateReferenceAsync(string owner, string repository, string reference, string sha)
        {
            CreatedReferences.Add(new CreatedReference(owner, repository, reference, sha));
            return Task.FromResult(reference);
        }

        public Task<string> CreateLinkedBranchAsync(string owner, string repository, int issueNumber, string baseOid, string branchName, CancellationToken cancellationToken)
        {
            LinkedBranchCalls.Add(new LinkedBranchCall(owner, repository, issueNumber, baseOid, branchName));
            if (ThrowLinkedBranchAlreadyExists)
            {
                throw new ApiException("branch already exists", System.Net.HttpStatusCode.UnprocessableEntity);
            }

            if (ThrowLinkedBranchUnexpectedly)
            {
                throw new NullReferenceException("GraphQL linked branch failed.");
            }

            return Task.FromResult(branchName);
        }

        public Task<IReadOnlyList<PullRequest>> ListPullRequestsAsync(string owner, string repository, string headOwner, string headBranch)
            => Task.FromResult<IReadOnlyList<PullRequest>>([]);

        public Task<PullRequest> GetPullRequestAsync(string owner, string repository, int number)
            => Task.FromResult(Model<PullRequest>(
                (nameof(PullRequest.HtmlUrl), $"https://github.com/{owner}/{repository}/pull/{number}"),
                (nameof(PullRequest.State), ItemState.Open),
                (nameof(PullRequest.Merged), false)));

        public Task<PullRequest> CreatePullRequestAsync(string owner, string repository, string title, string head, string baseBranch, string body)
        {
            CreatedPullRequest = new NewPullRequest(title, head, baseBranch) { Body = body, Draft = false };
            return Task.FromResult(Model<PullRequest>((nameof(PullRequest.HtmlUrl), $"https://github.com/{owner}/{repository}/pull/123")));
        }

        public Task<IReadOnlyList<PullRequestReviewComment>> GetPullRequestReviewCommentsAsync(string owner, string repository, int number)
            => Task.FromResult<IReadOnlyList<PullRequestReviewComment>>(ReviewComments);

        public Task ReactToIssueCommentAsync(string owner, string repository, long commentId, string reaction)
        {
            ReactionCalls.Add(new ReactionCall("issue-comment", owner, repository, commentId, reaction));
            return Task.CompletedTask;
        }

        public Task ReactToPullRequestReviewCommentAsync(string owner, string repository, long commentId, string reaction)
        {
            ReactionCalls.Add(new ReactionCall("review-comment", owner, repository, commentId, reaction));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RepositoryContent>> GetContentsByRefAsync(string owner, string repository, string path, string reference)
            => throw new NotFoundException("not found", System.Net.HttpStatusCode.NotFound);

        public Task CreateFileAsync(string owner, string repository, string path, string message, string content, string branch)
        {
            CreatedFiles.Add(new CreatedFile(path, content, branch));
            return Task.CompletedTask;
        }

        public Task UpdateFileAsync(string owner, string repository, string path, string message, string content, string sha, string branch)
            => Task.CompletedTask;
    }

    private static T Model<T>(params (string Property, object? Value)[] values)
    {
        var model = Activator.CreateInstance<T>();
        foreach (var (propertyName, value) in values)
        {
            var property = typeof(T).GetProperty(propertyName)!;
            property.SetValue(model, value);
        }

        return model;
    }

    private static User GitHubUser(string login)
        => Model<User>((nameof(User.Login), login));

    private sealed record LinkedBranchCall(string Owner, string Repository, int IssueNumber, string BaseOid, string BranchName);
    private sealed record CreatedReference(string Owner, string Repository, string BranchName, string Sha);
    private sealed record CreatedIssueComment(string Owner, string Repository, int Number, string Body);
    private sealed record CreatedFile(string Path, string Content, string Branch);
    private sealed record ReactionCall(string Kind, string Owner, string Repository, long SubjectId, string Reaction);

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class CapturingAgentRunner : IAgentRunner
    {
        public AgentTask? LastTask { get; private set; }

        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken cancellationToken)
        {
            LastTask = task;
            var result = new AgentRunResult(true, $"captured-{task.Kind.ToString().ToLowerInvariant()}-{task.WorkflowId:N}", $"Captured {task.Kind} output", null);
            return Task.FromResult(new AgentRunStartResult(result.ExternalId, result));
        }

        public Task<AgentRunResult?> TryGetResultAsync(string externalId, CancellationToken cancellationToken)
            => Task.FromResult<AgentRunResult?>(null);
    }

    private sealed class DeferredAgentRunner : IAgentRunner
    {
        public List<AgentTask> StartedTasks { get; } = [];
        public List<string> StartedExternalIds { get; } = [];
        public Dictionary<string, AgentRunResult?> Results { get; } = [];

        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken cancellationToken)
        {
            var externalId = $"deferred-{task.Kind.ToString().ToLowerInvariant()}-{StartedTasks.Count}";
            StartedTasks.Add(task);
            StartedExternalIds.Add(externalId);
            return Task.FromResult(new AgentRunStartResult(externalId));
        }

        public Task<AgentRunResult?> TryGetResultAsync(string externalId, CancellationToken cancellationToken)
            => Task.FromResult(Results.TryGetValue(externalId, out var result) ? result : null);
    }
}

public sealed class WorkerAgentMessageServiceTests
{
    [Fact]
    public async Task RecordAsync_Appends_agent_json_lines_to_running_task_output()
    {
        var store = new InMemoryWorkflowStore();
        var workflow = await store.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = "https://github.com/acme/widgets/issues/1",
            RepositoryUrl = "https://github.com/acme/widgets"
        }, CancellationToken.None);
        await store.UpsertTaskRunAsync(new TaskRun
        {
            WorkflowId = workflow.Id,
            Kind = TaskRunKind.Plan,
            Status = TaskRunStatus.Running,
            ExternalId = "formicae-plan-test"
        }, CancellationToken.None);

        var service = new WorkerAgentMessageService(store);
        var timestamp = DateTimeOffset.Parse("2026-06-26T12:00:00Z");
        var recorded = await service.RecordAsync(new WorkerAgentMessageRequest(
            workflow.Id,
            "Plan",
            "formicae-plan-test",
            "stdout",
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Planning now\"}}",
            timestamp), CancellationToken.None);

        var run = await store.GetTaskRunAsync(workflow.Id, TaskRunKind.Plan, CancellationToken.None);

        Assert.True(recorded);
        Assert.NotNull(run);
        Assert.Contains("Planning now", run.Output);
        Assert.Equal(timestamp, run.UpdatedAt);
    }

    [Fact]
    public async Task RecordAsync_Logs_worker_stderr_against_task_run()
    {
        var store = new InMemoryWorkflowStore();
        var workflow = await store.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = "https://github.com/acme/widgets/issues/1",
            RepositoryUrl = "https://github.com/acme/widgets"
        }, CancellationToken.None);
        var taskRun = await store.UpsertTaskRunAsync(new TaskRun
        {
            WorkflowId = workflow.Id,
            Kind = TaskRunKind.Implement,
            Status = TaskRunStatus.Running,
            ExternalId = "formicae-implement-test"
        }, CancellationToken.None);

        var service = new WorkerAgentMessageService(store);
        var recorded = await service.RecordAsync(new WorkerAgentMessageRequest(
            workflow.Id,
            "Implement",
            "formicae-implement-test",
            "stderr",
            "agent warning",
            DateTimeOffset.UtcNow), CancellationToken.None);

        var logs = await store.ListLogsAsync(workflow.Id, CancellationToken.None);

        Assert.True(recorded);
        var log = Assert.Single(logs);
        Assert.Equal(taskRun.Id, log.TaskRunId);
        Assert.Equal("Warning", log.Level);
        Assert.Equal("agent warning", log.Message);
    }

    [Fact]
    public async Task RecordAuthRefreshAsync_updates_codex_auth_for_matching_task_run()
    {
        var workflowStore = new InMemoryWorkflowStore();
        var settingsStore = new InMemoryAiSettingsStore();
        var settingsService = new AiSettingsService(settingsStore, Options.Create(new OpenHandsOptions()), new SystemClock());
        await settingsService.UpdateAsync(new UpdateAiSettingsRequest(
            AuthMethod: OpenHandsAuthMethods.CodexSubscription,
            CodexAuthJson: "{\"tokens\":\"old\"}",
            Id: "codex-ai",
            Name: "Codex AI"), CancellationToken.None);
        var workflow = await workflowStore.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = "https://github.com/acme/widgets/issues/1",
            RepositoryUrl = "https://github.com/acme/widgets"
        }, CancellationToken.None);
        await workflowStore.UpsertTaskRunAsync(new TaskRun
        {
            WorkflowId = workflow.Id,
            Kind = TaskRunKind.Implement,
            Status = TaskRunStatus.Running,
            ExternalId = "formicae-implement-test"
        }, CancellationToken.None);

        var service = new WorkerAgentAuthRefreshService(workflowStore, settingsService);
        var recorded = await service.RecordAsync(new WorkerAgentAuthRefreshRequest(
            workflow.Id,
            "Implement",
            "formicae-implement-test",
            "codex-ai",
            "{\"tokens\":\"new\"}"), CancellationToken.None);

        var resolved = await settingsService.ResolveAsync(CancellationToken.None);
        Assert.True(recorded);
        Assert.Equal("{\"tokens\":\"new\"}", resolved.CodexAuthJson);
    }

    [Fact]
    public async Task RecordAuthRefreshAsync_rejects_mismatched_external_job_id()
    {
        var workflowStore = new InMemoryWorkflowStore();
        var settingsStore = new InMemoryAiSettingsStore();
        var settingsService = new AiSettingsService(settingsStore, Options.Create(new OpenHandsOptions()), new SystemClock());
        await settingsService.UpdateAsync(new UpdateAiSettingsRequest(
            AuthMethod: OpenHandsAuthMethods.CodexSubscription,
            CodexAuthJson: "{\"tokens\":\"old\"}",
            Id: "codex-ai"), CancellationToken.None);
        var workflow = await workflowStore.CreateWorkflowAsync(new Workflow
        {
            IssueUrl = "https://github.com/acme/widgets/issues/1",
            RepositoryUrl = "https://github.com/acme/widgets"
        }, CancellationToken.None);
        await workflowStore.UpsertTaskRunAsync(new TaskRun
        {
            WorkflowId = workflow.Id,
            Kind = TaskRunKind.Implement,
            Status = TaskRunStatus.Running,
            ExternalId = "formicae-implement-test"
        }, CancellationToken.None);

        var service = new WorkerAgentAuthRefreshService(workflowStore, settingsService);
        var recorded = await service.RecordAsync(new WorkerAgentAuthRefreshRequest(
            workflow.Id,
            "Implement",
            "other-job",
            "codex-ai",
            "{\"tokens\":\"new\"}"), CancellationToken.None);

        var resolved = await settingsService.ResolveAsync(CancellationToken.None);
        Assert.False(recorded);
        Assert.Equal("{\"tokens\":\"old\"}", resolved.CodexAuthJson);
    }
}

public sealed class AdapterContractTests
{
    [Fact]
    public void Kubernetes_manifest_contains_container_environment_and_command()
    {
        var manifest = KubernetesJobManifest.Render(new RuntimeJobSpec(
            "formicae-plan-test",
            "worker:test",
            new Dictionary<string, string> { ["FORMICAE_TASK_KIND"] = "Plan" },
            ["dotnet", "hhnl.Formicae.Worker.dll"]));

        Assert.Contains("kind: Job", manifest);
        Assert.Contains("image: worker:test", manifest);
        Assert.Contains("name: FORMICAE_TASK_KIND", manifest);
        Assert.Contains("dotnet", manifest);
        Assert.Contains("hhnl.Formicae.Worker.dll", manifest);
    }

    [Fact]
    public async Task OpenHands_runner_builds_headless_json_job()
    {
        var jobRunner = new CapturingJobRunner();
        var runner = new OpenHandsAgentRunner(
            jobRunner,
            Options.Create(new RuntimeJobOptions { Image = "worker:test" }),
            Options.Create(new OpenHandsOptions { DefaultModel = "test-model" }));

        var start = await runner.StartAsync(new AgentTask(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            TaskRunKind.Plan,
            "Plan this",
            "https://github.com/acme/widgets",
            "formicae/test",
            null), CancellationToken.None);
        var result = await runner.TryGetResultAsync(start.ExternalId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.NotNull(jobRunner.LastSpec);
        Assert.Equal("worker:test", jobRunner.LastSpec.Image);
        Assert.Equal(RuntimeJobAuthMethods.ApiKey, jobRunner.LastSpec.AuthMethod);
        Assert.Equal(["dotnet", "hhnl.Formicae.Worker.dll"], jobRunner.LastSpec.Command);
        Assert.Equal("test-model", jobRunner.LastSpec.Environment["LLM_MODEL"]);
        Assert.Equal("Plan this", jobRunner.LastSpec.Environment["FORMICAE_TASK_PROMPT"]);
        Assert.Equal(OpenHandsAuthMethods.ApiKey, jobRunner.LastSpec.Environment["FORMICAE_OPENHANDS_AUTH_METHOD"]);
    }

    [Fact]
    public async Task OpenHands_runner_uses_saved_model_and_endpoint_for_new_jobs()
    {
        var settingsStore = new InMemoryAiSettingsStore();
        var settingsService = new AiSettingsService(
            settingsStore,
            Options.Create(new OpenHandsOptions()),
            new SystemClock());
        await settingsService.UpdateAsync(new UpdateAiSettingsRequest(
            "OpenAI",
            "saved-model",
            "https://llm.example.com/v1",
            OpenHandsAuthMethods.ApiKey,
            "llm-secret"), CancellationToken.None);
        var jobRunner = new CapturingJobRunner();
        var runner = new OpenHandsAgentRunner(
            jobRunner,
            Options.Create(new RuntimeJobOptions { Image = "worker:test" }),
            Options.Create(new OpenHandsOptions { DefaultModel = "option-model" }),
            settingsService);

        await runner.StartAsync(new AgentTask(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            TaskRunKind.Plan,
            "Plan this",
            "https://github.com/acme/widgets",
            "formicae/test",
            null), CancellationToken.None);

        Assert.NotNull(jobRunner.LastSpec);
        Assert.Equal("saved-model", jobRunner.LastSpec.Environment["FORMICAE_MODEL"]);
        Assert.Equal("saved-model", jobRunner.LastSpec.Environment["LLM_MODEL"]);
        Assert.Equal("https://llm.example.com/v1", jobRunner.LastSpec.Environment["LLM_BASE_URL"]);
    }

    [Fact]
    public async Task OpenHands_runner_passes_api_key_secret_environment_to_runtime_spec()
    {
        var settingsStore = new InMemoryAiSettingsStore();
        var settingsService = new AiSettingsService(settingsStore, Options.Create(new OpenHandsOptions()), new SystemClock());
        await settingsService.UpdateAsync(new UpdateAiSettingsRequest(
            AuthMethod: OpenHandsAuthMethods.ApiKey,
            LlmApiKey: "secret-key",
            ApiKeyEnvironmentVariable: "CUSTOM_API_KEY",
            Id: "api-ai",
            Name: "API AI"), CancellationToken.None);
        var jobRunner = new CapturingJobRunner();
        var runner = new OpenHandsAgentRunner(
            jobRunner,
            Options.Create(new RuntimeJobOptions { Image = "worker:test" }),
            Options.Create(new OpenHandsOptions()),
            settingsService);

        await runner.StartAsync(new AgentTask(
            Guid.Parse("12121212-1212-1212-1212-121212121212"),
            TaskRunKind.Plan,
            "Plan this",
            "https://github.com/acme/widgets",
            "formicae/test",
            null), CancellationToken.None);

        Assert.NotNull(jobRunner.LastSpec);
        Assert.NotNull(jobRunner.LastSpec.SecretEnvironment);
        Assert.Equal("secret-key", jobRunner.LastSpec.SecretEnvironment.Data["CUSTOM_API_KEY"]);
        Assert.False(jobRunner.LastSpec.Environment.ContainsKey("CUSTOM_API_KEY"));
    }

    [Fact]
    public async Task OpenHands_runner_uses_prompt_hash_and_unique_nonce_in_job_name()
    {
        var workflowId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var firstJobRunner = new CapturingJobRunner();
        var secondJobRunner = new CapturingJobRunner();
        var repeatedJobRunner = new CapturingJobRunner();
        var runner = new OpenHandsAgentRunner(
            firstJobRunner,
            Options.Create(new RuntimeJobOptions { Image = "worker:test" }),
            Options.Create(new OpenHandsOptions { DefaultModel = "test-model" }));

        await runner.StartAsync(new AgentTask(
            workflowId,
            TaskRunKind.AddressComments,
            "Address first comment",
            "https://github.com/acme/widgets",
            "formicae/test",
            null), CancellationToken.None);
        runner = new OpenHandsAgentRunner(
            secondJobRunner,
            Options.Create(new RuntimeJobOptions { Image = "worker:test" }),
            Options.Create(new OpenHandsOptions { DefaultModel = "test-model" }));
        await runner.StartAsync(new AgentTask(
            workflowId,
            TaskRunKind.AddressComments,
            "Address second comment",
            "https://github.com/acme/widgets",
            "formicae/test",
            null), CancellationToken.None);
        runner = new OpenHandsAgentRunner(
            repeatedJobRunner,
            Options.Create(new RuntimeJobOptions { Image = "worker:test" }),
            Options.Create(new OpenHandsOptions { DefaultModel = "test-model" }));
        await runner.StartAsync(new AgentTask(
            workflowId,
            TaskRunKind.AddressComments,
            "Address first comment",
            "https://github.com/acme/widgets",
            "formicae/test",
            null), CancellationToken.None);

        Assert.NotNull(firstJobRunner.LastSpec);
        Assert.NotNull(secondJobRunner.LastSpec);
        Assert.NotNull(repeatedJobRunner.LastSpec);
        Assert.NotEqual(firstJobRunner.LastSpec.Name, secondJobRunner.LastSpec.Name);
        Assert.NotEqual(firstJobRunner.LastSpec.Name, repeatedJobRunner.LastSpec.Name);
        Assert.True(firstJobRunner.LastSpec.Name.Length <= 63);
        Assert.StartsWith("formicae-addresscomments-11111111111111111111", firstJobRunner.LastSpec.Name);
        Assert.Matches("-[a-f0-9]{8}-[a-f0-9]{8}$", firstJobRunner.LastSpec.Name);
    }

    [Fact]
    public async Task OpenHands_runner_extracts_final_agent_message_from_json_logs()
    {
        var jobRunner = new CapturingJobRunner
        {
            Result = new RuntimeJobResult(true, "formicae-plan-test", """
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
            Options.Create(new RuntimeJobOptions { Image = "worker:test" }),
            Options.Create(new OpenHandsOptions { DefaultModel = "test-model" }));

        var start = await runner.StartAsync(new AgentTask(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            TaskRunKind.Plan,
            "Plan this",
            "https://github.com/acme/widgets",
            "formicae/test",
            null), CancellationToken.None);

        var result = await runner.TryGetResultAsync(start.ExternalId, CancellationToken.None);

        Assert.NotNull(result);
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
            Options.Create(new RuntimeJobOptions { Image = "worker:test" }),
            Options.Create(new OpenHandsOptions
            {
                AuthMethod = OpenHandsAuthMethods.CodexSubscription,
                DefaultModel = "gpt-5.2-codex",
                CodexSubscriptionImage = "node:22-bookworm-slim",
                CodexSubscriptionBootstrapCommand = "install git",
                CodexSubscriptionCommand = "run codex"
            }));

        var start = await runner.StartAsync(new AgentTask(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            TaskRunKind.Implement,
            "Implement this",
            "https://github.com/acme/widgets",
            "formicae/test",
            null), CancellationToken.None);
        var result = await runner.TryGetResultAsync(start.ExternalId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.NotNull(jobRunner.LastSpec);
        Assert.Equal("worker:test", jobRunner.LastSpec.Image);
        Assert.Equal(RuntimeJobAuthMethods.CodexSubscription, jobRunner.LastSpec.AuthMethod);
        Assert.Equal(["dotnet", "hhnl.Formicae.Worker.dll"], jobRunner.LastSpec.Command);
        Assert.Equal(OpenHandsAuthMethods.CodexSubscription, jobRunner.LastSpec.Environment["FORMICAE_OPENHANDS_AUTH_METHOD"]);
        Assert.Equal("gpt-5.2-codex", jobRunner.LastSpec.Environment["FORMICAE_MODEL"]);
        Assert.Equal("default", jobRunner.LastSpec.Environment["FORMICAE_AI_SETTINGS_ID"]);
        Assert.Equal("/tmp/codex-home", jobRunner.LastSpec.Environment["CODEX_HOME"]);
        Assert.Equal("/root/.codex", jobRunner.LastSpec.Environment["FORMICAE_CODEX_AUTH_MOUNT_PATH"]);
        Assert.Equal("auth.json", jobRunner.LastSpec.Environment["FORMICAE_CODEX_AUTH_FILE_NAME"]);
        Assert.Equal("""{"model":"gpt-5.2-codex"}""", jobRunner.LastSpec.Environment["CODEX_CONFIG"]);
        Assert.False(jobRunner.LastSpec.Environment.ContainsKey("LLM_MODEL"));
    }

    [Fact]
    public async Task OpenHands_runner_mounts_refreshed_codex_auth_before_original_subscription_credentials()
    {
        var settingsStore = new InMemoryAiSettingsStore();
        var settingsService = new AiSettingsService(settingsStore, Options.Create(new OpenHandsOptions()), new SystemClock());
        await settingsService.UpdateAsync(new UpdateAiSettingsRequest(
            AuthMethod: OpenHandsAuthMethods.CodexSubscription,
            SubscriptionCredentialJson: "{\"tokens\":\"old\"}",
            CodexAuthJson: "{\"tokens\":\"new\"}",
            Id: "codex-ai",
            Name: "Codex AI"), CancellationToken.None);
        var jobRunner = new CapturingJobRunner();
        var runner = new OpenHandsAgentRunner(
            jobRunner,
            Options.Create(new RuntimeJobOptions { Image = "worker:test" }),
            Options.Create(new OpenHandsOptions()),
            settingsService);

        await runner.StartAsync(new AgentTask(
            Guid.Parse("88888888-8888-8888-8888-888888888888"),
            TaskRunKind.Implement,
            "Implement this",
            "https://github.com/acme/widgets",
            "formicae/test",
            null), CancellationToken.None);

        Assert.NotNull(jobRunner.LastSpec);
        Assert.NotNull(jobRunner.LastSpec.SecretFiles);
        var secretFile = Assert.Single(jobRunner.LastSpec.SecretFiles);
        Assert.Equal("{\"tokens\":\"new\"}", secretFile.Data["auth.json"]);
    }

    [Fact]
    public async Task OpenHands_runner_codex_subscription_checks_out_and_pushes_address_comments()
    {
        var jobRunner = new CapturingJobRunner();
        var runner = new OpenHandsAgentRunner(
            jobRunner,
            Options.Create(new RuntimeJobOptions { Image = "worker:test" }),
            Options.Create(new OpenHandsOptions
            {
                AuthMethod = OpenHandsAuthMethods.CodexSubscription,
                DefaultModel = null,
                CodexSubscriptionBootstrapCommand = string.Empty
            }));

        var start = await runner.StartAsync(new AgentTask(
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            TaskRunKind.AddressComments,
            "Address comments",
            "https://github.com/acme/widgets",
            "formicae/test",
            null), CancellationToken.None);
        var result = await runner.TryGetResultAsync(start.ExternalId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.NotNull(jobRunner.LastSpec);
        Assert.Equal("worker:test", jobRunner.LastSpec.Image);
        Assert.Equal(RuntimeJobAuthMethods.CodexSubscription, jobRunner.LastSpec.AuthMethod);
        Assert.Equal(["dotnet", "hhnl.Formicae.Worker.dll"], jobRunner.LastSpec.Command);
        Assert.Equal("AddressComments", jobRunner.LastSpec.Environment["FORMICAE_TASK_KIND"]);
        Assert.Equal("https://github.com/acme/widgets", jobRunner.LastSpec.Environment["FORMICAE_REPOSITORY_URL"]);
        Assert.Equal("formicae/test", jobRunner.LastSpec.Environment["FORMICAE_BRANCH"]);
    }
    [Fact]
    public async Task OpenHands_runner_injects_github_installation_token_for_repository_work()
    {
        var jobRunner = new CapturingJobRunner();
        var integrationStore = new InMemoryDevOpsIntegrationStore();
        var integration = await integrationStore.CreateAsync(new DevOpsIntegration
        {
            ProviderType = DevOpsProviderType.GitHub,
            DisplayName = "GitHub",
            GitHubAppClientId = "client-id",
            GitHubAppPrivateKey = "private-key",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        await integrationStore.AddRepositoryAsync(new ConnectedRepository
        {
            DevOpsIntegrationId = integration.Id,
            Owner = "acme",
            Name = "widgets",
            RepositoryUrl = "https://github.com/acme/widgets",
            DefaultBranch = "main",
            InstallationId = 123
        }, CancellationToken.None);
        var gitHubAppClient = new RecordingOpenHandsGitHubAppClient("installation-token");
        var runner = new OpenHandsAgentRunner(
            jobRunner,
            Options.Create(new RuntimeJobOptions { Image = "worker:test" }),
            Options.Create(new OpenHandsOptions { AuthMethod = OpenHandsAuthMethods.CodexSubscription }),
            null,
            integrationStore,
            gitHubAppClient);

        await runner.StartAsync(new AgentTask(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            TaskRunKind.Implement,
            "Implement this",
            "https://github.com/acme/widgets",
            "formicae/test",
            null), CancellationToken.None);

        Assert.NotNull(jobRunner.LastSpec);
        Assert.Equal("installation-token", jobRunner.LastSpec.Environment["FORMICAE_GIT_ACCESS_TOKEN"]);
        Assert.Equal(integration.Id, gitHubAppClient.IntegrationId);
        Assert.Equal(123, gitHubAppClient.InstallationId);
    }
    [Fact]
    public async Task OpenHands_runner_allows_codex_subscription_without_configured_model()
    {
        var jobRunner = new CapturingJobRunner();
        var runner = new OpenHandsAgentRunner(
            jobRunner,
            Options.Create(new RuntimeJobOptions { Image = "worker:test" }),
            Options.Create(new OpenHandsOptions
            {
                AuthMethod = OpenHandsAuthMethods.CodexSubscription,
                DefaultModel = null,
                CodexSubscriptionImage = "node:22-bookworm-slim",
                CodexSubscriptionCommand = "run codex"
            }));

        var start = await runner.StartAsync(new AgentTask(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            TaskRunKind.Plan,
            "Plan this",
            "https://github.com/acme/widgets",
            "formicae/test",
            null), CancellationToken.None);
        var result = await runner.TryGetResultAsync(start.ExternalId, CancellationToken.None);

        Assert.NotNull(result);
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
            Options.Create(new RuntimeJobOptions { Image = "worker:test" }),
            Options.Create(new OpenHandsOptions { AuthMethod = "Unknown" }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.StartAsync(new AgentTask(
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
            LlmApiKeySecretName = "openhands-llm-api-key",
            CodexAuthSecretName = "formicae-codex-auth"
        }), []);

        var start = await runner.StartJobAsync(new RuntimeJobSpec(
            "formicae-plan-test",
            "worker:test",
            new Dictionary<string, string> { ["FORMICAE_TASK_KIND"] = "Plan" },
            ["dotnet", "hhnl.Formicae.Worker.dll"]), CancellationToken.None);
        var result = await runner.TryGetJobResultAsync(start.ExternalId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.Null(result.FailureReason);
        Assert.Contains("agent output", result.Logs);
        Assert.NotNull(api.CreatedJob);
        Assert.Equal("formicae-plan-test", api.CreatedJob.Metadata.Name);
        var container = Assert.Single(api.CreatedJob.Spec.Template.Spec.Containers);
        Assert.Equal("worker:test", container.Image);
        Assert.Contains(container.Env, env => env.Name == "FORMICAE_TASK_KIND" && env.Value == "Plan");
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
            LlmApiKeySecretName = "openhands-llm-api-key",
            CodexAuthSecretName = "formicae-codex-auth"
        }), []);

        var start = await runner.StartJobAsync(new RuntimeJobSpec(
            "formicae-plan-test",
            "node:22-bookworm-slim",
            new Dictionary<string, string> { ["CODEX_HOME"] = "/tmp/codex-home" },
            ["/bin/sh", "-lc", "run codex"],
            RuntimeJobAuthMethods.CodexSubscription), CancellationToken.None);
        var result = await runner.TryGetJobResultAsync(start.ExternalId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.Null(result.FailureReason);
        Assert.Contains("agent output", result.Logs);
        Assert.NotNull(api.CreatedJob);
        var container = Assert.Single(api.CreatedJob.Spec.Template.Spec.Containers);
        Assert.Equal("node:22-bookworm-slim", container.Image);
        Assert.Contains(container.Env, env => env.Name == "CODEX_HOME" && env.Value == "/tmp/codex-home");
        Assert.Null(container.EnvFrom);
        Assert.Contains(container.VolumeMounts, mount => mount.Name == "codex-auth" && mount.MountPath == "/root/.codex");
        Assert.Contains(api.CreatedJob.Spec.Template.Spec.Volumes, volume => volume.Secret.SecretName == "formicae-codex-auth");
    }

    [Fact]
    public async Task Kubernetes_runner_returns_waiting_message_when_pod_logs_are_not_ready()
    {
        var api = new CapturingKubernetesJobApi
        {
            Pods =
            [
                new k8s.Models.V1Pod
                {
                    Metadata = new k8s.Models.V1ObjectMeta
                    {
                        Name = "formicae-codex-login-pod",
                        CreationTimestamp = DateTime.UtcNow
                    }
                }
            ],
            PodLogException = new InvalidOperationException("container \"worker\" is waiting to start: ContainerCreating")
        };
        var runner = new KubernetesJobRunner(api, Options.Create(new KubernetesJobOptions
        {
            Namespace = "formicae",
            PollIntervalSeconds = 1,
            TimeoutSeconds = 5
        }), []);

        var logs = await runner.ReadJobLogsAsync("formicae-codex-login", CancellationToken.None);

        Assert.Contains("--- pod/formicae-codex-login-pod logs ---", logs);
        Assert.Contains("Container is starting", logs);
        Assert.DoesNotContain("Unable to read logs", logs);
    }
    [Fact]
    public async Task Kubernetes_runner_mounts_context_configmap_owned_by_job()
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
                        Name = "formicae-address-comments-pod",
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
            DeleteFinishedJobs = true
        }), []);

        var start = await runner.StartJobAsync(new RuntimeJobSpec(
            "formicae-address-comments",
            "formicae-agent:test",
            new Dictionary<string, string>(),
            ["/bin/sh", "-lc", "run agent"],
            ContextFiles:
            [
                new RuntimeJobContextFile("pull-request-conversation.md", "# Conversation")
            ]), CancellationToken.None);
        var result = await runner.TryGetJobResultAsync(start.ExternalId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.NotNull(api.CreatedJob);
        Assert.NotNull(api.CreatedConfigMap);
        Assert.Equal("formicae-address-comments-context", api.CreatedConfigMap.Metadata.Name);
        Assert.Equal("# Conversation", api.CreatedConfigMap.Data["pull-request-conversation.md"]);
        var ownerReference = Assert.Single(api.CreatedConfigMap.Metadata.OwnerReferences);
        Assert.Equal("Job", ownerReference.Kind);
        Assert.Equal("formicae-address-comments", ownerReference.Name);
        Assert.Equal("formicae-address-comments-uid", ownerReference.Uid);
        var container = Assert.Single(api.CreatedJob.Spec.Template.Spec.Containers);
        Assert.Contains(container.VolumeMounts, mount => mount.Name == "formicae-context" && mount.MountPath == "/workspace/formicae/context" && mount.ReadOnlyProperty == true);
        Assert.Contains(api.CreatedJob.Spec.Template.Spec.Volumes, volume => volume.Name == "formicae-context" && volume.ConfigMap.Name == "formicae-address-comments-context");
        Assert.Collection(api.DeletedJobs, name => Assert.Equal("formicae-address-comments", name));
        Assert.Collection(api.DeletedConfigMaps, name => Assert.Equal("formicae-address-comments-context", name));
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
        }), []);

        var start = await runner.StartJobAsync(new RuntimeJobSpec(
            "formicae-plan-test",
            "worker:test",
            new Dictionary<string, string>(),
            ["dotnet", "hhnl.Formicae.Worker.dll"]), CancellationToken.None);
        var result = await runner.TryGetJobResultAsync(start.ExternalId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Equal("The agent container failed.", result.FailureReason);
        Assert.Contains("agent error", result.Logs);
    }

    [Fact]
    public void Infrastructure_registers_container_runtime_when_job_runtime_is_unset()
    {
        var services = new ServiceCollection();
        services.AddFormicaeInfrastructure(BuildInfrastructureConfiguration(new Dictionary<string, string?>
        {
            ["UseFakeAdapters"] = "false",
            ["PersistenceMode"] = "InMemory",
            ["WorkItemMode"] = "Fake",
            ["SourceControlMode"] = "Fake",
            ["AgentMode"] = "OpenHands"
        }));

        var descriptor = Assert.Single(services, service => service.ServiceType == typeof(IJobRuntime));
        Assert.Equal(typeof(ContainerJobRuntime), descriptor.ImplementationType);
    }

    [Fact]
    public void Infrastructure_registers_kubernetes_runtime_when_selected()
    {
        var services = new ServiceCollection();
        services.AddFormicaeInfrastructure(BuildInfrastructureConfiguration(new Dictionary<string, string?>
        {
            ["UseFakeAdapters"] = "false",
            ["PersistenceMode"] = "InMemory",
            ["WorkItemMode"] = "Fake",
            ["SourceControlMode"] = "Fake",
            ["AgentMode"] = "OpenHands",
            ["JobRuntime"] = "Kubernetes"
        }));

        var descriptor = Assert.Single(services, service => service.ServiceType == typeof(IJobRuntime));
        Assert.Equal(typeof(KubernetesJobRunner), descriptor.ImplementationType);
    }

    [Fact]
    public void Infrastructure_fake_adapters_register_fake_agent_runner()
    {
        var provider = new ServiceCollection()
            .AddFormicaeInfrastructure(BuildInfrastructureConfiguration(new Dictionary<string, string?>
            {
                ["UseFakeAdapters"] = "true"
            }))
            .BuildServiceProvider();

        Assert.IsType<FakeAgentRunner>(provider.GetRequiredService<IAgentRunner>());
    }

    [Fact]
    public async Task Infrastructure_discovery_registration_scans_connected_repositories()
    {
        await using var provider = new ServiceCollection()
            .AddFormicaeInfrastructure(BuildInfrastructureConfiguration(new Dictionary<string, string?>
            {
                ["UseFakeAdapters"] = "false",
                ["PersistenceMode"] = "InMemory",
                ["WorkItemMode"] = "Fake",
                ["SourceControlMode"] = "Fake",
                ["AgentMode"] = "Fake",
                ["WorkflowDiscovery:Enabled"] = "true"
            }))
            .BuildServiceProvider();
        using var scope = provider.CreateScope();
        var integrationStore = scope.ServiceProvider.GetRequiredService<IDevOpsIntegrationStore>();
        var integration = await integrationStore.CreateAsync(new DevOpsIntegration
        {
            ProviderType = DevOpsProviderType.GitHub,
            DisplayName = "GitHub",
            GitHubAppClientId = "client-id",
            CreatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z")
        }, CancellationToken.None);
        await integrationStore.AddRepositoryAsync(new ConnectedRepository
        {
            DevOpsIntegrationId = integration.Id,
            Owner = "acme",
            Name = "widgets",
            RepositoryUrl = "https://github.com/acme/widgets",
            DefaultBranch = "develop",
            CreatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z")
        }, CancellationToken.None);
        await integrationStore.AddRepositoryAsync(new ConnectedRepository
        {
            DevOpsIntegrationId = integration.Id,
            Owner = "acme",
            Name = "tools",
            RepositoryUrl = "https://github.com/acme/tools",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z")
        }, CancellationToken.None);

        var discovered = await scope.ServiceProvider
            .GetRequiredService<WorkflowDiscoveryService>()
            .DiscoverReadyToPlanWorkflowsAsync(CancellationToken.None);

        var workflows = await scope.ServiceProvider
            .GetRequiredService<IWorkflowStore>()
            .ListRunnableWorkflowsAsync(CancellationToken.None);
        Assert.Equal(2, discovered);
        Assert.Contains(workflows, workflow => workflow.RepositoryUrl == "https://github.com/acme/widgets" && workflow.BaseBranch == "develop");
        Assert.Contains(workflows, workflow => workflow.RepositoryUrl == "https://github.com/acme/tools" && workflow.BaseBranch == "main");
    }

    [Fact]
    public async Task Container_runtime_starts_container_with_env_command_mounts_and_labels()
    {
        using var workspace = new TemporaryDirectory();
        var cli = new CapturingContainerCli();
        var runtime = new ContainerJobRuntime(cli, Options.Create(new ContainerRuntimeOptions
        {
            Image = "worker:test",
            WorkspaceRoot = workspace.Path,
            Network = "formicae-net",
            Executable = "podman"
        }), []);

        var start = await runtime.StartJobAsync(new RuntimeJobSpec(
            "formicae-plan-test",
            "worker:test",
            new Dictionary<string, string> { ["FORMICAE_TASK_KIND"] = "Plan" },
            ["dotnet", "hhnl.Formicae.Worker.dll"],
            ContextFiles: [new RuntimeJobContextFile("pull-request-conversation.md", "# Conversation")],
            SecretFiles: [new RuntimeJobSecretFile("codex-auth", "/root/.codex", new Dictionary<string, string> { ["auth.json"] = "{}" })],
            SecretEnvironment: new RuntimeJobSecretEnvironment("api-auth", new Dictionary<string, string> { ["LLM_API_KEY"] = "secret" })), CancellationToken.None);

        Assert.Equal("formicae-plan-test", start.ExternalId);
        var run = Assert.Single(cli.Calls, call => call.Arguments.FirstOrDefault() == "run");
        Assert.Equal("podman", run.Executable);
        Assert.Contains("--detach", run.Arguments);
        Assert.Contains("formicae.managed-by=formicae", run.Arguments);
        Assert.Contains("formicae.job=formicae-plan-test", run.Arguments);
        Assert.Contains("FORMICAE_TASK_KIND=Plan", run.Arguments);
        Assert.Contains("LLM_API_KEY=secret", run.Arguments);
        Assert.Contains("formicae-net", run.Arguments);
        Assert.Contains("worker:test", run.Arguments);
        Assert.Contains("dotnet", run.Arguments);
        Assert.True(File.Exists(System.IO.Path.Combine(workspace.Path, "formicae-plan-test", "context", "pull-request-conversation.md")));
        Assert.True(File.Exists(System.IO.Path.Combine(workspace.Path, "formicae-plan-test", "secrets", "codex-auth", "auth.json")));
    }

    [Fact]
    public async Task Container_runtime_returns_null_while_container_is_running()
    {
        var cli = new CapturingContainerCli();
        cli.InspectResults.Enqueue(ContainerInspectJson(running: true, exitCode: 0, DateTimeOffset.UtcNow));
        var runtime = new ContainerJobRuntime(cli, Options.Create(new ContainerRuntimeOptions()), []);

        var result = await runtime.TryGetJobResultAsync("formicae-plan-test", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Container_runtime_returns_success_and_failure_from_exit_code_with_logs()
    {
        var cli = new CapturingContainerCli { Logs = "agent output" };
        cli.InspectResults.Enqueue(ContainerInspectJson(running: false, exitCode: 0, DateTimeOffset.UtcNow));
        cli.InspectResults.Enqueue(ContainerInspectJson(running: false, exitCode: 2, DateTimeOffset.UtcNow));
        var runtime = new ContainerJobRuntime(cli, Options.Create(new ContainerRuntimeOptions { DeleteFinishedContainers = false }), []);

        var success = await runtime.TryGetJobResultAsync("formicae-plan-success", CancellationToken.None);
        var failure = await runtime.TryGetJobResultAsync("formicae-plan-failure", CancellationToken.None);

        Assert.NotNull(success);
        Assert.True(success.Succeeded);
        Assert.Equal("agent output", success.Logs);
        Assert.NotNull(failure);
        Assert.False(failure.Succeeded);
        Assert.Equal("Container 'formicae-plan-failure' exited with code 2.", failure.FailureReason);
        Assert.Equal(2, cli.Calls.Count(call => call.Arguments.FirstOrDefault() == "logs"));
    }

    [Fact]
    public async Task Container_runtime_handles_timeout()
    {
        var cli = new CapturingContainerCli { Logs = "running output" };
        cli.InspectResults.Enqueue(ContainerInspectJson(running: true, exitCode: 0, DateTimeOffset.UtcNow.AddSeconds(-60)));
        var runtime = new ContainerJobRuntime(cli, Options.Create(new ContainerRuntimeOptions
        {
            TimeoutSeconds = 1,
            DeleteFinishedContainers = true
        }), []);

        var result = await runtime.TryGetJobResultAsync("formicae-plan-timeout", CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Equal("running output", result.Logs);
        Assert.Contains("timed out after 1 seconds", result.FailureReason);
        Assert.Contains(cli.Calls, call => call.Arguments.SequenceEqual(["rm", "--force", "formicae-plan-timeout"]));
    }

    [Fact]
    public async Task Container_runtime_removes_finished_containers_when_configured()
    {
        var cli = new CapturingContainerCli();
        cli.InspectResults.Enqueue(ContainerInspectJson(running: false, exitCode: 0, DateTimeOffset.UtcNow));
        var runtime = new ContainerJobRuntime(cli, Options.Create(new ContainerRuntimeOptions { DeleteFinishedContainers = true }), []);

        await runtime.TryGetJobResultAsync("formicae-plan-test", CancellationToken.None);

        Assert.Contains(cli.Calls, call => call.Arguments.SequenceEqual(["rm", "formicae-plan-test"]));
    }

    private sealed class RecordingOpenHandsGitHubAppClient(string token) : IGitHubAppClient
    {
        public Guid? IntegrationId { get; private set; }
        public long? InstallationId { get; private set; }

        public Task<GitHubAppMetadata> GetAppMetadataAsync(DevOpsIntegration integration, CancellationToken cancellationToken)
            => Task.FromResult(new GitHubAppMetadata("formicae-test", "https://github.com/apps/formicae-test"));

        public Task<IReadOnlyList<GitHubInstallationRepository>> ListInstallationRepositoriesAsync(DevOpsIntegration integration, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<GitHubInstallationRepository>>([]);

        public Task<string> CreateInstallationTokenAsync(DevOpsIntegration integration, long installationId, CancellationToken cancellationToken)
        {
            IntegrationId = integration.Id;
            InstallationId = installationId;
            return Task.FromResult(token);
        }
    }

    [Fact]
    public async Task CodexAuthSetupService_sanitizes_device_login_output()
    {
        var settingsService = new AiSettingsService(new InMemoryAiSettingsStore(), Options.Create(new OpenHandsOptions()), new SystemClock());
        var jobRunner = new CapturingJobRunner
        {
            Result = new RuntimeJobResult(
                true,
                "formicae-codex-login",
                "Follow these steps\n   \u001b[94mhttps://auth.openai.com/codex/device\u001b[0m\n   \u001b[94mE4UQ-ZWLG0\u001b[0m\n",
                null)
        };
        var service = new CodexAuthSetupService(
            jobRunner,
            Options.Create(new RuntimeJobOptions { Image = "worker:test" }),
            Options.Create(new OpenHandsOptions()),
            settingsService);

        var status = await service.GetStatusAsync("codex-ai", "formicae-codex-login", CancellationToken.None);

        Assert.Equal("https://auth.openai.com/codex/device", status.DeviceLoginUrl);
        Assert.Equal("E4UQ-ZWLG0", status.DeviceLoginCode);
        Assert.True(!status.Output.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'), string.Join(",", status.Output.Select(character => ((int)character).ToString())));
        Assert.Contains("https://auth.openai.com/codex/device", status.Output);
        Assert.Contains("E4UQ-ZWLG0", status.Output);
    }
    [Fact]
    public async Task CodexAuthSetupService_uses_device_auth_login_by_default()
    {
        var settingsStore = new InMemoryAiSettingsStore();
        var settingsService = new AiSettingsService(settingsStore, Options.Create(new OpenHandsOptions()), new SystemClock());
        await settingsService.UpdateAsync(new UpdateAiSettingsRequest(
            AuthMethod: OpenHandsAuthMethods.CodexSubscription,
            Id: "codex-ai",
            Name: "Codex AI"), CancellationToken.None);
        var jobRunner = new CapturingJobRunner();
        var service = new CodexAuthSetupService(
            jobRunner,
            Options.Create(new RuntimeJobOptions { Image = "worker:test" }),
            Options.Create(new OpenHandsOptions()),
            settingsService);

        await service.StartAsync("codex-ai", CancellationToken.None);

        Assert.NotNull(jobRunner.LastSpec);
        Assert.Equal("npx -y @openai/codex login --device-auth", jobRunner.LastSpec.Environment["FORMICAE_CODEX_LOGIN_COMMAND"]);
    }

    [Fact]
    public async Task CodexAuthSetupService_starts_ephemeral_login_job_for_ai_settings()
    {
        var settingsStore = new InMemoryAiSettingsStore();
        var settingsService = new AiSettingsService(settingsStore, Options.Create(new OpenHandsOptions()), new SystemClock());
        await settingsService.UpdateAsync(new UpdateAiSettingsRequest(
            AuthMethod: OpenHandsAuthMethods.CodexSubscription,
            CodexAuthJson: "{\"tokens\":\"old\"}",
            Id: "codex-ai",
            Name: "Codex AI"), CancellationToken.None);
        var jobRunner = new CapturingJobRunner();
        var service = new CodexAuthSetupService(
            jobRunner,
            Options.Create(new RuntimeJobOptions
            {
                Image = "worker:test",
                WorkerCallbackUrl = "http://formicae-api/api/worker/agent-messages",
                WorkerCallbackSecret = "callback-secret"
            }),
            Options.Create(new OpenHandsOptions { CodexSubscriptionLoginCommand = "codex login --device" }),
            settingsService);

        var start = await service.StartAsync("codex-ai", CancellationToken.None);

        Assert.Equal("codex-ai", start.AiSettingsId);
        Assert.Equal("Running", start.Status);
        Assert.NotNull(jobRunner.LastSpec);
        Assert.StartsWith("formicae-codex-login-", jobRunner.LastSpec.Name);
        Assert.Equal("worker:test", jobRunner.LastSpec.Image);
        Assert.Equal(RuntimeJobAuthMethods.None, jobRunner.LastSpec.AuthMethod);
        Assert.Equal(["dotnet", "hhnl.Formicae.Worker.dll"], jobRunner.LastSpec.Command);
        Assert.Equal("CodexAuthSetup", jobRunner.LastSpec.Environment["FORMICAE_TASK_KIND"]);
        Assert.Equal("CodexSubscriptionSetup", jobRunner.LastSpec.Environment["FORMICAE_OPENHANDS_AUTH_METHOD"]);
        Assert.Equal("codex-ai", jobRunner.LastSpec.Environment["FORMICAE_AI_SETTINGS_ID"]);
        Assert.Equal("/tmp/codex-home", jobRunner.LastSpec.Environment["CODEX_HOME"]);
        Assert.Equal("1", jobRunner.LastSpec.Environment["NO_COLOR"]);
        Assert.Equal("dumb", jobRunner.LastSpec.Environment["TERM"]);
        Assert.Equal("codex login --device", jobRunner.LastSpec.Environment["FORMICAE_CODEX_LOGIN_COMMAND"]);
        Assert.Equal("callback-secret", jobRunner.LastSpec.Environment["FORMICAE_WORKER_CALLBACK_SECRET"]);
    }

    private sealed class CapturingJobRunner : IJobRuntime
    {
        public RuntimeJobSpec? LastSpec { get; private set; }
        public RuntimeJobResult? Result { get; init; }

        public Task<RuntimeJobStartResult> StartJobAsync(RuntimeJobSpec spec, CancellationToken cancellationToken)
        {
            LastSpec = spec;
            return Task.FromResult(new RuntimeJobStartResult(spec.Name));
        }

        public Task<RuntimeJobResult?> TryGetJobResultAsync(string jobName, CancellationToken cancellationToken)
            => Task.FromResult<RuntimeJobResult?>(Result ?? new RuntimeJobResult(true, jobName, "ok", null));

        public Task<string> ReadJobLogsAsync(string jobName, CancellationToken cancellationToken)
            => Task.FromResult(Result?.Logs ?? "running logs");
    }

    private static IConfiguration BuildInfrastructureConfiguration(IReadOnlyDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static string ContainerInspectJson(bool running, int exitCode, DateTimeOffset startedAt)
        => $$"""
            [
              {
                "State": {
                  "Running": {{running.ToString().ToLowerInvariant()}},
                  "ExitCode": {{exitCode}},
                  "StartedAt": "{{startedAt:O}}"
                }
              }
            ]
            """;

    private sealed class CapturingContainerCli : IContainerCli
    {
        public List<ContainerCliCall> Calls { get; } = [];
        public Queue<string> InspectResults { get; } = new();
        public string Logs { get; init; } = string.Empty;

        public Task<ContainerCliResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(new ContainerCliCall(executable, arguments.ToArray()));
            return arguments.FirstOrDefault() switch
            {
                "inspect" => Task.FromResult(new ContainerCliResult(0, InspectResults.Dequeue(), string.Empty)),
                "logs" => Task.FromResult(new ContainerCliResult(0, Logs, string.Empty)),
                _ => Task.FromResult(new ContainerCliResult(0, "ok", string.Empty))
            };
        }
    }

    private sealed record ContainerCliCall(string Executable, IReadOnlyList<string> Arguments);

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"formicae-tests-{Guid.NewGuid():N}");

        public TemporaryDirectory()
            => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class CapturingKubernetesJobApi : IKubernetesJobApi
    {
        public k8s.Models.V1Job? CreatedJob { get; private set; }
        public k8s.Models.V1ConfigMap? CreatedConfigMap { get; private set; }
        public List<string> DeletedJobs { get; } = [];
        public List<string> DeletedConfigMaps { get; } = [];
        public List<k8s.Models.V1Secret> CreatedSecrets { get; } = [];
        public List<string> DeletedSecrets { get; } = [];
        public Queue<k8s.Models.V1Job> Statuses { get; init; } = new();
        public IReadOnlyList<k8s.Models.V1Pod> Pods { get; init; } = [];
        public string PodLogs { get; init; } = string.Empty;
        public Exception? PodLogException { get; init; }

        public Task<k8s.Models.V1Job> CreateJobAsync(k8s.Models.V1Job job, string namespaceName, CancellationToken cancellationToken)
        {
            job.Metadata ??= new k8s.Models.V1ObjectMeta();
            job.Metadata.Uid ??= $"{job.Metadata.Name}-uid";
            CreatedJob = job;
            return Task.FromResult(job);
        }

        public Task CreateConfigMapAsync(k8s.Models.V1ConfigMap configMap, string namespaceName, CancellationToken cancellationToken)
        {
            CreatedConfigMap = configMap;
            return Task.CompletedTask;
        }

        public Task<k8s.Models.V1Job> ReadJobStatusAsync(string name, string namespaceName, CancellationToken cancellationToken)
            => Task.FromResult(Statuses.Count == 0
                ? new k8s.Models.V1Job { Status = new k8s.Models.V1JobStatus { Succeeded = 1 } }
                : Statuses.Dequeue());

        public Task<IReadOnlyList<k8s.Models.V1Pod>> ListPodsAsync(string namespaceName, string labelSelector, CancellationToken cancellationToken)
            => Task.FromResult(Pods);

        public Task<string> ReadPodLogAsync(string name, string namespaceName, string container, CancellationToken cancellationToken)
            => PodLogException is null ? Task.FromResult(PodLogs) : Task.FromException<string>(PodLogException);

        public Task CreateSecretAsync(k8s.Models.V1Secret secret, string namespaceName, CancellationToken cancellationToken)
        {
            CreatedSecrets.Add(secret);
            return Task.CompletedTask;
        }

        public Task DeleteSecretAsync(string name, string namespaceName, CancellationToken cancellationToken)
        {
            DeletedSecrets.Add(name);
            return Task.CompletedTask;
        }

        public Task DeleteJobAsync(string name, string namespaceName, CancellationToken cancellationToken)
        {
            DeletedJobs.Add(name);
            return Task.CompletedTask;
        }

        public Task DeleteConfigMapAsync(string name, string namespaceName, CancellationToken cancellationToken)
        {
            DeletedConfigMaps.Add(name);
            return Task.CompletedTask;
        }
    }
}
