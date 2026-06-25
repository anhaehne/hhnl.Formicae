namespace hhnl.Formicae.Infrastructure.Kubernetes;

using System.Text;
using k8s;
using k8s.Models;

public interface IKubernetesJobRunner
{
    Task<KubernetesJobResult> RunJobAsync(KubernetesJobSpec spec, CancellationToken cancellationToken);
}

public sealed record KubernetesJobSpec(string Name, string Image, IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> Command);
public sealed record KubernetesJobResult(bool Succeeded, string JobName, string Logs, string? FailureReason);

public sealed class KubernetesJobOptions
{
    public string Namespace { get; set; } = "default";
    public string Image { get; set; } = "ghcr.io/openhands/openhands:latest";
    public string WorkspaceVolumeClaim { get; set; } = "formicae-workspaces";
    public int TimeoutSeconds { get; set; } = 1800;
    public int PollIntervalSeconds { get; set; } = 5;
    public bool DeleteFinishedJobs { get; set; }
    public string RuntimeSecretName { get; set; } = "formicae-runtime-secrets";
    public string LlmApiKeySecretName { get; set; } = "openhands-llm-api-key";
    public string CodexAuthSecretName { get; set; } = string.Empty;
    public string CodexAuthSecretKey { get; set; } = "auth.json";
    public string CodexAuthMountPath { get; set; } = "/root/.codex";
}

public interface IKubernetesJobApi
{
    Task CreateJobAsync(V1Job job, string namespaceName, CancellationToken cancellationToken);
    Task<V1Job> ReadJobStatusAsync(string name, string namespaceName, CancellationToken cancellationToken);
    Task<IReadOnlyList<V1Pod>> ListPodsAsync(string namespaceName, string labelSelector, CancellationToken cancellationToken);
    Task<string> ReadPodLogAsync(string name, string namespaceName, string container, CancellationToken cancellationToken);
    Task DeleteJobAsync(string name, string namespaceName, CancellationToken cancellationToken);
}

public sealed class KubernetesJobApi : IKubernetesJobApi, IDisposable
{
    private readonly Kubernetes client;

    public KubernetesJobApi()
    {
        var configuration = KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();
        client = new Kubernetes(configuration);
    }

    public Task CreateJobAsync(V1Job job, string namespaceName, CancellationToken cancellationToken)
        => client.BatchV1.CreateNamespacedJobAsync(job, namespaceName, cancellationToken: cancellationToken);

    public Task<V1Job> ReadJobStatusAsync(string name, string namespaceName, CancellationToken cancellationToken)
        => client.BatchV1.ReadNamespacedJobStatusAsync(name, namespaceName, cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<V1Pod>> ListPodsAsync(string namespaceName, string labelSelector, CancellationToken cancellationToken)
    {
        var pods = await client.CoreV1.ListNamespacedPodAsync(namespaceName, labelSelector: labelSelector, cancellationToken: cancellationToken);
        return pods.Items.ToList();
    }

    public async Task<string> ReadPodLogAsync(string name, string namespaceName, string container, CancellationToken cancellationToken)
    {
        await using var stream = await client.CoreV1.ReadNamespacedPodLogAsync(name, namespaceName, container: container, cancellationToken: cancellationToken);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public Task DeleteJobAsync(string name, string namespaceName, CancellationToken cancellationToken)
        => client.BatchV1.DeleteNamespacedJobAsync(
            name,
            namespaceName,
            new V1DeleteOptions { PropagationPolicy = "Background" },
            cancellationToken: cancellationToken);

    public void Dispose()
        => client.Dispose();
}

public sealed class KubernetesJobRunner(IKubernetesJobApi jobApi, Microsoft.Extensions.Options.IOptions<KubernetesJobOptions> options) : IKubernetesJobRunner
{
    private const string ContainerName = "openhands";
    private const string ManagedByLabel = "app.kubernetes.io/managed-by";
    private const string ManagedByValue = "formicae";

    public async Task<KubernetesJobResult> RunJobAsync(KubernetesJobSpec spec, CancellationToken cancellationToken)
    {
        var namespaceName = string.IsNullOrWhiteSpace(options.Value.Namespace) ? "default" : options.Value.Namespace;
        var manifest = KubernetesJobManifest.Render(spec);
        var job = BuildJob(spec);

        try
        {
            await jobApi.CreateJobAsync(job, namespaceName, cancellationToken);
        }
        catch (KubernetesException exception) when (exception.Status.Code == 409)
        {
            // A retry should attach to the existing deterministic Job rather than duplicate work.
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Value.TimeoutSeconds)));

        try
        {
            while (true)
            {
                var current = await jobApi.ReadJobStatusAsync(spec.Name, namespaceName, timeout.Token);
                if (IsComplete(current))
                {
                    var logs = await ReadLogsAsync(spec.Name, namespaceName, timeout.Token);
                    await DeleteIfConfiguredAsync(spec.Name, namespaceName, cancellationToken);
                    return new KubernetesJobResult(true, spec.Name, Combine(manifest, logs), null);
                }

                if (IsFailed(current, out var failureReason))
                {
                    var logs = await ReadLogsAsync(spec.Name, namespaceName, timeout.Token);
                    await DeleteIfConfiguredAsync(spec.Name, namespaceName, cancellationToken);
                    return new KubernetesJobResult(false, spec.Name, Combine(manifest, logs), failureReason);
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds)), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var logs = await ReadLogsAsync(spec.Name, namespaceName, CancellationToken.None);
            return new KubernetesJobResult(false, spec.Name, Combine(manifest, logs), $"Kubernetes job '{spec.Name}' timed out after {options.Value.TimeoutSeconds} seconds.");
        }
    }

    private V1Job BuildJob(KubernetesJobSpec spec)
    {
        var labels = new Dictionary<string, string>
        {
            [ManagedByLabel] = ManagedByValue,
            ["app.kubernetes.io/name"] = "formicae-agent-job",
            ["formicae.hhnl.de/task"] = spec.Name
        };

        var envFrom = new List<V1EnvFromSource>();
        AddOptionalSecretEnvFrom(envFrom, options.Value.RuntimeSecretName);
        AddOptionalSecretEnvFrom(envFrom, options.Value.LlmApiKeySecretName);

        var volumes = new List<V1Volume>();
        var volumeMounts = new List<V1VolumeMount>();
        if (!string.IsNullOrWhiteSpace(options.Value.CodexAuthSecretName))
        {
            volumes.Add(new V1Volume
            {
                Name = "codex-auth",
                Secret = new V1SecretVolumeSource
                {
                    SecretName = options.Value.CodexAuthSecretName,
                    Items =
                    [
                        new V1KeyToPath
                        {
                            Key = options.Value.CodexAuthSecretKey,
                            Path = options.Value.CodexAuthSecretKey
                        }
                    ]
                }
            });
            volumeMounts.Add(new V1VolumeMount
            {
                Name = "codex-auth",
                MountPath = options.Value.CodexAuthMountPath,
                ReadOnlyProperty = true
            });
        }

        return new V1Job
        {
            ApiVersion = "batch/v1",
            Kind = "Job",
            Metadata = new V1ObjectMeta { Name = spec.Name, Labels = labels },
            Spec = new V1JobSpec
            {
                BackoffLimit = 0,
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = labels },
                    Spec = new V1PodSpec
                    {
                        RestartPolicy = "Never",
                        Volumes = volumes.Count == 0 ? null : volumes,
                        Containers =
                        [
                            new V1Container
                            {
                                Name = ContainerName,
                                Image = spec.Image,
                                ImagePullPolicy = "IfNotPresent",
                                Env = spec.Environment
                                    .OrderBy(pair => pair.Key)
                                    .Select(pair => new V1EnvVar { Name = pair.Key, Value = pair.Value })
                                    .ToList(),
                                EnvFrom = envFrom.Count == 0 ? null : envFrom,
                                Command = spec.Command.ToList(),
                                VolumeMounts = volumeMounts.Count == 0 ? null : volumeMounts
                            }
                        ]
                    }
                }
            }
        };
    }

    private static void AddOptionalSecretEnvFrom(List<V1EnvFromSource> envFrom, string secretName)
    {
        if (string.IsNullOrWhiteSpace(secretName))
        {
            return;
        }

        envFrom.Add(new V1EnvFromSource { SecretRef = new V1SecretEnvSource { Name = secretName, Optional = true } });
    }

    private static bool IsComplete(V1Job job)
        => job.Status?.Succeeded > 0 || HasCondition(job, "Complete");

    private static bool IsFailed(V1Job job, out string reason)
    {
        var failedCondition = job.Status?.Conditions?.FirstOrDefault(condition =>
            string.Equals(condition.Type, "Failed", StringComparison.OrdinalIgnoreCase)
            && string.Equals(condition.Status, "True", StringComparison.OrdinalIgnoreCase));
        if (failedCondition is not null)
        {
            reason = failedCondition.Message ?? failedCondition.Reason ?? "Kubernetes job failed.";
            return true;
        }

        if (job.Status?.Failed > 0)
        {
            reason = "Kubernetes job failed.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool HasCondition(V1Job job, string type)
        => job.Status?.Conditions?.Any(condition =>
            string.Equals(condition.Type, type, StringComparison.OrdinalIgnoreCase)
            && string.Equals(condition.Status, "True", StringComparison.OrdinalIgnoreCase)) == true;

    private async Task<string> ReadLogsAsync(string jobName, string namespaceName, CancellationToken cancellationToken)
    {
        var pods = await jobApi.ListPodsAsync(namespaceName, $"job-name={jobName}", cancellationToken);
        var builder = new StringBuilder();
        foreach (var pod in pods.OrderBy(pod => pod.Metadata.CreationTimestamp))
        {
            var podName = pod.Metadata.Name;
            if (string.IsNullOrWhiteSpace(podName))
            {
                continue;
            }

            builder.AppendLine($"--- pod/{podName} logs ---");
            try
            {
                builder.AppendLine(await jobApi.ReadPodLogAsync(podName, namespaceName, ContainerName, cancellationToken));
            }
            catch (KubernetesException exception)
            {
                builder.AppendLine($"Unable to read logs: {exception.Message}");
            }
        }

        return builder.ToString();
    }

    private async Task DeleteIfConfiguredAsync(string jobName, string namespaceName, CancellationToken cancellationToken)
    {
        if (options.Value.DeleteFinishedJobs)
        {
            await jobApi.DeleteJobAsync(jobName, namespaceName, cancellationToken);
        }
    }

    private static string Combine(string manifest, string logs)
        => string.IsNullOrWhiteSpace(logs)
            ? manifest
            : $"{manifest}{Environment.NewLine}{logs}";
}
