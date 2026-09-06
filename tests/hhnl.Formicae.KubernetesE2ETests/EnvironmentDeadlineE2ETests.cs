using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.Kubernetes;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.KubernetesE2ETests;

public sealed partial class KubernetesWorkflowE2ETests
{
    [Fact]
    public async Task Environment_cap_terminates_real_job_without_runtime_polling_and_cleans_up()
    {
        await WithDiagnosticsAsync(async () =>
        {
            const string ns = "formicae";
            var name = $"environment-cap-{Guid.NewGuid():N}";
            using var api = new FixtureJobApi(fixture.KubeconfigPath);
            var runtime = new KubernetesJobRunner(api, Options.Create(new KubernetesJobOptions
                { Namespace = ns, TimeoutSeconds = 120, DeleteFinishedJobs = true }), []);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                // Reuse the image already built/loaded by this isolated fixture; no external registry or live cluster is involved.
                var spec = new RuntimeJobSpec(name, "localhost/hhnl-formicae-api:e2e",
                    new Dictionary<string, string>(), ["/bin/sh", "-c", "echo environment-cap-started; sleep 60"],
                    AuthMethod: RuntimeJobAuthMethods.None, TimeoutLimitSeconds: 10);
                Assert.Null(spec.ExecutionPolicy);
                await runtime.StartJobAsync(spec, timeout.Token);
                var job = await api.ReadJobStatusAsync(name, ns, timeout.Token);
                Assert.Equal(10, job.Spec.ActiveDeadlineSeconds);
                var env = Assert.Single(job.Spec.Template.Spec.Containers).Env;
                Assert.Contains(env, item => item.Name == "FORMICAE_ENVIRONMENT_TIMEOUT_LIMIT" && item.Value == "true");
                Assert.Contains(env, item => item.Name == "FORMICAE_JOB_TIMEOUT_SECONDS" && item.Value == "10");
                Assert.Contains(env, item => item.Name == "FORMICAE_CHECKPOINT_GRACE_SECONDS" && item.Value == "0");
                // Observe the native controller directly: runtime polling must not be the cause of termination.
                var observedStarted = false;
                while (job.Status?.Conditions?.Any(condition => condition.Type == "Failed" && condition.Status == "True") != true)
                {
                    if (!observedStarted && (await api.ListPodsAsync(ns, $"job-name={name}", timeout.Token))
                        .Any(pod => pod.Status?.Phase == "Running"))
                        observedStarted = (await runtime.ReadJobLogsAsync(name, timeout.Token)).Contains("environment-cap-started", StringComparison.Ordinal);
                    await Task.Delay(500, timeout.Token);
                    job = await api.ReadJobStatusAsync(name, ns, timeout.Token);
                }
                // Native deadline handling can remove Pods before the failed Job condition is visible.
                Assert.True(observedStarted, "The bounded process must have been observed running before native termination.");
                Assert.Contains(job.Status.Conditions, condition => condition.Type == "Failed" && condition.Reason == "DeadlineExceeded");
                var result = await runtime.TryGetJobResultAsync(name, timeout.Token);
                Assert.NotNull(result); Assert.False(result.Succeeded); Assert.Contains("deadline", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
                while (true)
                {
                    try { await api.ReadJobStatusAsync(name, ns, timeout.Token); }
                    catch (k8s.Autorest.HttpOperationException exception) when (exception.Response?.StatusCode == System.Net.HttpStatusCode.NotFound) { break; }
                    await Task.Delay(500, timeout.Token);
                }
                while ((await api.ListPodsAsync(ns, $"job-name={name}", timeout.Token)).Count > 0)
                    await Task.Delay(500, timeout.Token);
            }
            finally
            {
                try { await api.DeleteJobAsync(name, ns, CancellationToken.None); }
                catch (k8s.Autorest.HttpOperationException exception) when (exception.Response?.StatusCode == System.Net.HttpStatusCode.NotFound) { }
            }
        });
    }

    private sealed class FixtureJobApi(string kubeconfigPath) : IKubernetesJobApi, IDisposable
    {
        private readonly Kubernetes client = new(KubernetesClientConfiguration.BuildConfigFromConfigFile(kubeconfigPath));
        public Task<V1Job> CreateJobAsync(V1Job job, string ns, CancellationToken token)
            => client.BatchV1.CreateNamespacedJobAsync(job, ns, cancellationToken: token);
        public Task<V1Job> ReadJobStatusAsync(string name, string ns, CancellationToken token)
            => client.BatchV1.ReadNamespacedJobStatusAsync(name, ns, cancellationToken: token);
        public async Task<IReadOnlyList<V1Pod>> ListPodsAsync(string ns, string selector, CancellationToken token)
            => (await client.CoreV1.ListNamespacedPodAsync(ns, labelSelector: selector, cancellationToken: token)).Items.ToArray();
        public async Task<string> ReadPodLogAsync(string name, string ns, string container, CancellationToken token)
        {
            await using var stream = await client.CoreV1.ReadNamespacedPodLogAsync(name, ns, container: container, cancellationToken: token);
            using var reader = new StreamReader(stream); return await reader.ReadToEndAsync(token);
        }
        public Task CreateConfigMapAsync(V1ConfigMap map, string ns, CancellationToken token)
            => client.CoreV1.CreateNamespacedConfigMapAsync(map, ns, cancellationToken: token);
        public Task CreateSecretAsync(V1Secret secret, string ns, CancellationToken token)
            => client.CoreV1.CreateNamespacedSecretAsync(secret, ns, cancellationToken: token);
        public Task DeleteSecretAsync(string name, string ns, CancellationToken token)
            => client.CoreV1.DeleteNamespacedSecretAsync(name, ns, cancellationToken: token);
        public Task DeleteConfigMapAsync(string name, string ns, CancellationToken token)
            => client.CoreV1.DeleteNamespacedConfigMapAsync(name, ns, cancellationToken: token);
        public Task DeleteJobAsync(string name, string ns, CancellationToken token)
            => client.BatchV1.DeleteNamespacedJobAsync(name, ns, new V1DeleteOptions { PropagationPolicy = "Background" }, cancellationToken: token);
        public void Dispose() => client.Dispose();
    }
}
