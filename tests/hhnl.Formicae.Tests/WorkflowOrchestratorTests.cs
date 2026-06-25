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
        Assert.Equal(KubernetesJobAuthMethods.ApiKey, jobRunner.LastSpec.AuthMethod);
        Assert.Equal(["/bin/sh", "-lc", "openhands --headless --json --override-with-envs -t \"$FORMICAE_TASK_PROMPT\""], jobRunner.LastSpec.Command);
        Assert.Equal("test-model", jobRunner.LastSpec.Environment["LLM_MODEL"]);
        Assert.Equal("Plan this", jobRunner.LastSpec.Environment["FORMICAE_TASK_PROMPT"]);
        Assert.Equal(OpenHandsAuthMethods.ApiKey, jobRunner.LastSpec.Environment["FORMICAE_OPENHANDS_AUTH_METHOD"]);
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

        public Task<KubernetesJobResult> RunJobAsync(KubernetesJobSpec spec, CancellationToken cancellationToken)
        {
            LastSpec = spec;
            return Task.FromResult(new KubernetesJobResult(true, spec.Name, "ok", null));
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
