using System.Text.Json;
using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.Containers;
using hhnl.Formicae.Infrastructure.Kubernetes;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Tests;

public sealed class CustomTaskRuntimePolicyTests
{
    private static RuntimeJobSpec Spec() => new("custom-deadline", "worker:test",
        new Dictionary<string, string> { ["FORMICAE_TASK_KIND"] = "Custom" }, ["worker"],
        ExecutionRequirements: new(), ExecutionPolicy: new(43, 0));

    [Fact]
    public async Task Kubernetes_propagates_custom_deadline_without_checkpoint_or_privileged_sidecars()
    {
        var api = new KubernetesApi(); var runtime = new KubernetesJobRunner(api, Options.Create(new KubernetesJobOptions()), []);
        await runtime.StartJobAsync(Spec(), default);
        var job = api.Job!; Assert.Equal(43, job.Spec.ActiveDeadlineSeconds);
        var worker = Assert.Single(job.Spec.Template.Spec.Containers);
        Assert.Contains(worker.Env, env => env.Name == "FORMICAE_JOB_TIMEOUT_SECONDS" && env.Value == "43");
        Assert.Contains(worker.Env, env => env.Name == "FORMICAE_CHECKPOINT_GRACE_SECONDS" && env.Value == "0");
        Assert.Null(job.Spec.Template.Spec.InitContainers); Assert.False(job.Spec.Template.Spec.AutomountServiceAccountToken);
        Assert.DoesNotContain(worker.Env, env => env.Name == "DOCKER_HOST");
    }

    [Fact]
    public async Task Local_runtime_propagates_custom_deadline_and_stops_overdue_container_using_stored_policy()
    {
        var cli = new Cli(); var runtime = new ContainerJobRuntime(cli, Options.Create(new ContainerRuntimeOptions()), []);
        await runtime.StartJobAsync(Spec(), default);
        var arguments = cli.Calls.Single();
        Assert.Contains("FORMICAE_JOB_TIMEOUT_SECONDS=43", arguments); Assert.Contains("FORMICAE_CHECKPOINT_GRACE_SECONDS=0", arguments);
        Assert.Contains("formicae.timeout-seconds=43", arguments); Assert.DoesNotContain("--privileged", arguments);
        var result = await runtime.TryGetJobResultAsync("custom-deadline", default);
        Assert.NotNull(result); Assert.False(result.Succeeded); Assert.Contains("43 seconds", result.FailureReason);
        Assert.Contains(cli.Calls, call => call[0] == "rm" && call.Contains("--force"));
    }

    private sealed class Cli : IContainerCli
    {
        public List<IReadOnlyList<string>> Calls = [];
        public Task<ContainerCliResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            Calls.Add(arguments.ToArray());
            return Task.FromResult(new ContainerCliResult(0, arguments[0] == "inspect" ? JsonSerializer.Serialize(new[] {
                new { State = new { Running = true, ExitCode = 0, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O") },
                    Config = new { Labels = new Dictionary<string, string> { ["formicae.timeout-seconds"] = "43" } } } }) : "container", ""));
        }
    }
    private sealed class KubernetesApi : IKubernetesJobApi
    {
        public V1Job? Job;
        public Task<V1Job> CreateJobAsync(V1Job job, string ns, CancellationToken token) { Job = job; return Task.FromResult(job); }
        public Task CreateConfigMapAsync(V1ConfigMap map, string ns, CancellationToken token) => Task.CompletedTask;
        public Task CreateSecretAsync(V1Secret secret, string ns, CancellationToken token) => Task.CompletedTask;
        public Task DeleteSecretAsync(string name, string ns, CancellationToken token) => Task.CompletedTask;
        public Task DeleteJobAsync(string name, string ns, CancellationToken token) => Task.CompletedTask;
        public Task DeleteConfigMapAsync(string name, string ns, CancellationToken token) => Task.CompletedTask;
        public Task<V1Job> ReadJobStatusAsync(string name, string ns, CancellationToken token) => throw new NotImplementedException();
        public Task<IReadOnlyList<V1Pod>> ListPodsAsync(string ns, string selector, CancellationToken token) => throw new NotImplementedException();
        public Task<string> ReadPodLogAsync(string name, string ns, string container, CancellationToken token) => throw new NotImplementedException();
    }
}
