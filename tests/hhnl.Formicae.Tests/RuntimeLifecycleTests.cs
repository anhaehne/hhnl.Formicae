using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.Kubernetes;
using hhnl.Formicae.Infrastructure.Containers;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Tests;

public sealed class RuntimeLifecycleTests
{
    [Fact]
    public async Task Kubernetes_finished_job_is_retained_until_explicit_durable_completion_acknowledgement()
    {
        var api = new Api { Job = new() { Status = new() { Succeeded = 1 } } };
        var runtime = new KubernetesJobRunner(api, Options.Create(new KubernetesJobOptions { DeleteFinishedJobs = true }), []);
        var result = await runtime.TryGetJobResultAsync("worker", default);
        Assert.NotNull(result); Assert.True(result.Succeeded); Assert.Equal(0, api.Deletes);
        Assert.Contains("retained output", result.Logs);
        await runtime.AcknowledgeCompletionAsync("worker", default);
        Assert.Equal(1, api.Deletes);
    }

    [Fact]
    public async Task Kubernetes_cancel_remains_pending_until_active_pods_terminate_even_when_retention_enabled()
    {
        var api = new Api { Active = true };
        var runtime = new KubernetesJobRunner(api, Options.Create(new KubernetesJobOptions { DeleteFinishedJobs = false }), []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CancelJobAsync("worker", default));
        Assert.Equal(1, api.Deletes);
        api.Active = false;
        await runtime.CancelJobAsync("worker", default);
        Assert.Equal(2, api.Deletes);
    }

    [Fact]
    public async Task Kubernetes_timeout_ack_stops_job_even_when_finished_job_retention_is_enabled()
    {
        var api = new Api { Job = new() { Metadata = new() { CreationTimestamp = DateTime.UtcNow.AddHours(-1) },
            Spec = new() { ActiveDeadlineSeconds = 1 }, Status = new() { Active = 1 } } };
        var runtime = new KubernetesJobRunner(api, Options.Create(new KubernetesJobOptions { DeleteFinishedJobs = false }), []);
        await runtime.AcknowledgeCompletionAsync("worker", default);
        Assert.Equal(1, api.Deletes);
    }

    [Fact]
    public async Task Container_cancel_failure_is_observable_and_stderr_is_preserved_in_recovery()
    {
        var cli = new Cli();
        var runtime = new ContainerJobRuntime(cli, Options.Create(new ContainerRuntimeOptions { DeleteFinishedContainers = false }), []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CancelJobAsync("worker", default));
        cli.StopFails = false;
        await runtime.CancelJobAsync("worker", default);
        var logs = await runtime.ReadJobLogsAsync("worker", default);
        Assert.Contains("stdout content", logs); Assert.Contains("stderr content", logs);
        await runtime.AcknowledgeCompletionAsync("worker", default);
        Assert.DoesNotContain("rm", cli.Commands);
    }

    private sealed class Api : IKubernetesJobApi
    {
        public V1Job Job = new(); public bool Active; public int Deletes;
        public Task<V1Job> CreateJobAsync(V1Job job, string ns, CancellationToken token) => Task.FromResult(job);
        public Task CreateConfigMapAsync(V1ConfigMap map, string ns, CancellationToken token) => Task.CompletedTask;
        public Task CreateSecretAsync(V1Secret secret, string ns, CancellationToken token) => Task.CompletedTask;
        public Task DeleteSecretAsync(string name, string ns, CancellationToken token) => Task.CompletedTask;
        public Task DeleteJobAsync(string name, string ns, CancellationToken token) { Deletes++; return Task.CompletedTask; }
        public Task DeleteConfigMapAsync(string name, string ns, CancellationToken token) => Task.CompletedTask;
        public Task<V1Job> ReadJobStatusAsync(string name, string ns, CancellationToken token) => Task.FromResult(Job);
        public Task<IReadOnlyList<V1Pod>> ListPodsAsync(string ns, string selector, CancellationToken token)
            => Task.FromResult<IReadOnlyList<V1Pod>>([new() { Metadata = new() { Name = "worker-pod" }, Status = new() { Phase = Active ? "Running" : "Succeeded" } }]);
        public Task<string> ReadPodLogAsync(string name, string ns, string container, CancellationToken token) => Task.FromResult("retained output");
    }
    private sealed class Cli : IContainerCli
    {
        public bool StopFails = true; public List<string> Commands = [];
        public Task<ContainerCliResult> RunAsync(string executable, IReadOnlyList<string> args, CancellationToken token)
        {
            Commands.Add(args[0]);
            return Task.FromResult(args[0] switch
            {
                "inspect" => new ContainerCliResult(0, "[{\"State\":{\"Running\":true,\"ExitCode\":0,\"StartedAt\":\"2026-10-06T00:00:00Z\"}}]", ""),
                "stop" when StopFails => new ContainerCliResult(1, "", "daemon unavailable"),
                "logs" => new ContainerCliResult(0, "stdout content", "stderr content"),
                _ => new ContainerCliResult(0, "", "")
            });
        }
    }
}
