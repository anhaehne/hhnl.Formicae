namespace hhnl.Formicae.Infrastructure.Kubernetes;

using System.Text;
using hhnl.Formicae.Application.Workflows;
using k8s;
using k8s.Models;

public interface IKubernetesJobRunner
{
    Task<KubernetesJobStartResult> StartJobAsync(KubernetesJobSpec spec, CancellationToken cancellationToken);

    Task<KubernetesJobResult?> TryGetJobResultAsync(string jobName, CancellationToken cancellationToken);

    Task<string> ReadJobLogsAsync(string jobName, CancellationToken cancellationToken);
}

public sealed record KubernetesJobSpec(
    string Name,
    string Image,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> Command,
    string AuthMethod = KubernetesJobAuthMethods.ApiKey,
    IReadOnlyList<KubernetesJobContextFile>? ContextFiles = null,
    string ContextFilesMountPath = "/workspace/formicae/context",
    IReadOnlyList<KubernetesJobSecretFile>? SecretFiles = null,
    KubernetesJobSecretEnvironment? SecretEnvironment = null);
public sealed record KubernetesJobContextFile(string FileName, string Content);
public sealed record KubernetesJobSecretFile(string SecretName, string MountPath, IReadOnlyDictionary<string, string> Data);
public sealed record KubernetesJobSecretEnvironment(string SecretName, IReadOnlyDictionary<string, string> Data);
public sealed record KubernetesJobStartResult(string JobName);
public sealed record KubernetesJobResult(bool Succeeded, string JobName, string Logs, string? FailureReason);

public static class KubernetesJobAuthMethods
{
    public const string None = "None";
    public const string ApiKey = "ApiKey";
    public const string CodexSubscription = "CodexSubscription";
}

public sealed class KubernetesJobOptions
{
    public string Namespace { get; set; } = "default";
    public string Image { get; set; } = "docker.io/limeray/hhnl-formicae-worker:latest";
    public string WorkspaceVolumeClaim { get; set; } = "formicae-workspaces";
    public int TimeoutSeconds { get; set; } = 1800;
    public int PollIntervalSeconds { get; set; } = 5;
    public bool DeleteFinishedJobs { get; set; }
    public string LlmApiKeySecretName { get; set; } = "openhands-llm-api-key";
    public string CodexAuthSecretName { get; set; } = string.Empty;
    public string CodexAuthSecretKey { get; set; } = "auth.json";
    public string CodexAuthMountPath { get; set; } = "/root/.codex";
    public string WorkerCallbackUrl { get; set; } = string.Empty;
    public string WorkerCallbackSecret { get; set; } = string.Empty;
}

public interface IKubernetesJobApi
{
    Task<V1Job> CreateJobAsync(V1Job job, string namespaceName, CancellationToken cancellationToken);
    Task CreateConfigMapAsync(V1ConfigMap configMap, string namespaceName, CancellationToken cancellationToken);
    Task<V1Job> ReadJobStatusAsync(string name, string namespaceName, CancellationToken cancellationToken);
    Task<IReadOnlyList<V1Pod>> ListPodsAsync(string namespaceName, string labelSelector, CancellationToken cancellationToken);
    Task<string> ReadPodLogAsync(string name, string namespaceName, string container, CancellationToken cancellationToken);
    Task CreateSecretAsync(V1Secret secret, string namespaceName, CancellationToken cancellationToken);
    Task DeleteSecretAsync(string name, string namespaceName, CancellationToken cancellationToken);
    Task DeleteJobAsync(string name, string namespaceName, CancellationToken cancellationToken);
    Task DeleteConfigMapAsync(string name, string namespaceName, CancellationToken cancellationToken);
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

    public Task<V1Job> CreateJobAsync(V1Job job, string namespaceName, CancellationToken cancellationToken)
        => client.BatchV1.CreateNamespacedJobAsync(job, namespaceName, cancellationToken: cancellationToken);

    public Task CreateConfigMapAsync(V1ConfigMap configMap, string namespaceName, CancellationToken cancellationToken)
        => client.CoreV1.CreateNamespacedConfigMapAsync(configMap, namespaceName, cancellationToken: cancellationToken);

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

    public Task CreateSecretAsync(V1Secret secret, string namespaceName, CancellationToken cancellationToken)
        => client.CoreV1.CreateNamespacedSecretAsync(secret, namespaceName, cancellationToken: cancellationToken);

    public Task DeleteSecretAsync(string name, string namespaceName, CancellationToken cancellationToken)
        => client.CoreV1.DeleteNamespacedSecretAsync(name, namespaceName, cancellationToken: cancellationToken);

    public Task DeleteJobAsync(string name, string namespaceName, CancellationToken cancellationToken)
        => client.BatchV1.DeleteNamespacedJobAsync(
            name,
            namespaceName,
            new V1DeleteOptions { PropagationPolicy = "Background" },
            cancellationToken: cancellationToken);

    public Task DeleteConfigMapAsync(string name, string namespaceName, CancellationToken cancellationToken)
        => client.CoreV1.DeleteNamespacedConfigMapAsync(
            name,
            namespaceName,
            new V1DeleteOptions { PropagationPolicy = "Background" },
            cancellationToken: cancellationToken);

    public void Dispose()
        => client.Dispose();
}

public sealed class KubernetesJobRunner(
    IKubernetesJobApi jobApi,
    Microsoft.Extensions.Options.IOptions<KubernetesJobOptions> options,
    IEnumerable<IWorkflowTickSignal> tickSignals) : IKubernetesJobRunner
{
    private const string ContainerName = "worker";
    private const string ManagedByLabel = "app.kubernetes.io/managed-by";
    private const string ManagedByValue = "formicae";

    public async Task<KubernetesJobStartResult> StartJobAsync(KubernetesJobSpec spec, CancellationToken cancellationToken)
    {
        var namespaceName = ResolveNamespace();
        var job = BuildJob(spec);
        await CreateSecretsAsync(spec, namespaceName, cancellationToken);
        V1Job createdJob;

        try
        {
            createdJob = await jobApi.CreateJobAsync(job, namespaceName, cancellationToken);
        }
        catch (Exception exception) when (IsAlreadyExistsConflict(exception))
        {
            // A retry should attach to the existing deterministic Job rather than duplicate work.
            createdJob = await jobApi.ReadJobStatusAsync(spec.Name, namespaceName, cancellationToken);
        }

        var contextConfigMap = BuildContextConfigMap(spec, createdJob);
        if (contextConfigMap is not null)
        {
            try
            {
                await jobApi.CreateConfigMapAsync(contextConfigMap, namespaceName, cancellationToken);
            }
            catch (Exception exception) when (IsAlreadyExistsConflict(exception))
            {
                // A retry can reuse the existing deterministic context ConfigMap.
            }
        }

        StartCompletionSignalWatcher(spec.Name, namespaceName);
        return new KubernetesJobStartResult(spec.Name);
    }

    public async Task<KubernetesJobResult?> TryGetJobResultAsync(string jobName, CancellationToken cancellationToken)
    {
        var namespaceName = ResolveNamespace();
        V1Job current;
        try
        {
            current = await jobApi.ReadJobStatusAsync(jobName, namespaceName, cancellationToken);
        }
        catch (Exception exception) when (IsNotFound(exception))
        {
            return new KubernetesJobResult(false, jobName, string.Empty, $"Kubernetes job '{jobName}' was not found.");
        }

        if (IsComplete(current))
        {
            var logs = await ReadLogsAsync(jobName, namespaceName, cancellationToken);
            await DeleteIfConfiguredAsync(jobName, namespaceName, cancellationToken);
            return new KubernetesJobResult(true, jobName, logs, null);
        }

        if (IsFailed(current, out var failureReason))
        {
            var logs = await ReadLogsAsync(jobName, namespaceName, cancellationToken);
            await DeleteIfConfiguredAsync(jobName, namespaceName, cancellationToken);
            return new KubernetesJobResult(false, jobName, logs, failureReason);
        }

        if (IsTimedOut(current, out var timeoutReason))
        {
            var logs = await ReadLogsAsync(jobName, namespaceName, CancellationToken.None);
            await DeleteIfConfiguredAsync(jobName, namespaceName, CancellationToken.None);
            return new KubernetesJobResult(false, jobName, logs, timeoutReason);
        }

        return null;
    }
    public async Task<string> ReadJobLogsAsync(string jobName, CancellationToken cancellationToken)
        => await ReadLogsAsync(jobName, ResolveNamespace(), cancellationToken);

    private string ResolveNamespace()
        => string.IsNullOrWhiteSpace(options.Value.Namespace) ? "default" : options.Value.Namespace;

    private bool IsTimedOut(V1Job job, out string reason)
    {
        var startedAt = job.Status?.StartTime ?? job.Metadata?.CreationTimestamp;
        if (startedAt is null)
        {
            reason = string.Empty;
            return false;
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.TimeoutSeconds));
        if (DateTimeOffset.UtcNow - startedAt.Value.ToUniversalTime() <= timeout)
        {
            reason = string.Empty;
            return false;
        }

        reason = $"Kubernetes job '{job.Metadata?.Name}' timed out after {options.Value.TimeoutSeconds} seconds.";
        return true;
    }

    private void StartCompletionSignalWatcher(string jobName, string namespaceName)
    {
        var signal = tickSignals.FirstOrDefault();
        if (signal is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var current = await jobApi.ReadJobStatusAsync(jobName, namespaceName, CancellationToken.None);
                    if (IsComplete(current) || IsFailed(current, out _) || IsTimedOut(current, out _))
                    {
                        signal.Signal();
                        return;
                    }
                }
                catch (Exception exception) when (IsNotFound(exception))
                {
                    return;
                }
                catch
                {
                    // The periodic orchestration loop is still the durable fallback.
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds)), CancellationToken.None);
            }
        });
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
        if (IsAuthMethod(spec.AuthMethod, KubernetesJobAuthMethods.ApiKey) && !string.IsNullOrWhiteSpace(options.Value.LlmApiKeySecretName))
        {
            envFrom.Add(new V1EnvFromSource { SecretRef = new V1SecretEnvSource { Name = options.Value.LlmApiKeySecretName, Optional = true } });
        }

        var volumes = new List<V1Volume>();
        var volumeMounts = new List<V1VolumeMount>();
        if (IsAuthMethod(spec.AuthMethod, KubernetesJobAuthMethods.CodexSubscription)
            && (spec.SecretFiles is null || spec.SecretFiles.Count == 0)
            && !string.IsNullOrWhiteSpace(options.Value.CodexAuthSecretName))
        {
            volumes.Add(new V1Volume
            {
                Name = "codex-auth",
                Secret = new V1SecretVolumeSource
                {
                    SecretName = options.Value.CodexAuthSecretName,
                    Items = [new V1KeyToPath { Key = options.Value.CodexAuthSecretKey, Path = options.Value.CodexAuthSecretKey }]
                }
            });
            volumeMounts.Add(new V1VolumeMount { Name = "codex-auth", MountPath = options.Value.CodexAuthMountPath, ReadOnlyProperty = true });
        }
        if (spec.SecretFiles?.Count > 0)
        {
            var index = 0;
            foreach (var secretFile in spec.SecretFiles)
            {
                var volumeName = $"formicae-secret-{index++}";
                volumes.Add(new V1Volume
                {
                    Name = volumeName,
                    Secret = new V1SecretVolumeSource
                    {
                        SecretName = secretFile.SecretName,
                        Items = secretFile.Data.Keys
                            .Select(key => new V1KeyToPath { Key = key, Path = key })
                            .ToList()
                    }
                });
                volumeMounts.Add(new V1VolumeMount
                {
                    Name = volumeName,
                    MountPath = secretFile.MountPath,
                    ReadOnlyProperty = true
                });
            }
        }

        if (spec.ContextFiles?.Count > 0)
        {
            volumes.Add(new V1Volume
            {
                Name = "formicae-context",
                ConfigMap = new V1ConfigMapVolumeSource
                {
                    Name = ContextConfigMapName(spec.Name),
                    Items = spec.ContextFiles
                        .Select(file => new V1KeyToPath { Key = file.FileName, Path = file.FileName })
                        .ToList()
                }
            });
            volumeMounts.Add(new V1VolumeMount
            {
                Name = "formicae-context",
                MountPath = spec.ContextFilesMountPath,
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
                                Env = BuildEnvironmentVariables(spec),
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

    private static List<V1EnvVar> BuildEnvironmentVariables(KubernetesJobSpec spec)
    {
        var env = spec.Environment
            .OrderBy(pair => pair.Key)
            .Select(pair => new V1EnvVar { Name = pair.Key, Value = pair.Value })
            .ToList();

        if (spec.SecretEnvironment is not null)
        {
            env.AddRange(spec.SecretEnvironment.Data.Keys.OrderBy(key => key).Select(key => new V1EnvVar
            {
                Name = key,
                ValueFrom = new V1EnvVarSource
                {
                    SecretKeyRef = new V1SecretKeySelector { Name = spec.SecretEnvironment.SecretName, Key = key }
                }
            }));
        }

        return env;
    }

    private async Task CreateSecretsAsync(KubernetesJobSpec spec, string namespaceName, CancellationToken cancellationToken)
    {
        var secrets = new List<(string Name, IReadOnlyDictionary<string, string> Data)>();
        if (spec.SecretEnvironment is not null)
        {
            secrets.Add((spec.SecretEnvironment.SecretName, spec.SecretEnvironment.Data));
        }

        if (spec.SecretFiles is not null)
        {
            secrets.AddRange(spec.SecretFiles.Select(secretFile => (secretFile.SecretName, secretFile.Data)));
        }

        foreach (var secretFile in secrets)
        {
            var secret = new V1Secret
            {
                ApiVersion = "v1",
                Kind = "Secret",
                Metadata = new V1ObjectMeta
                {
                    Name = secretFile.Name,
                    Labels = new Dictionary<string, string>
                    {
                        [ManagedByLabel] = ManagedByValue,
                        ["app.kubernetes.io/name"] = "formicae-agent-secret",
                        ["formicae.hhnl.de/task"] = spec.Name
                    }
                },
                Type = "Opaque",
                Data = secretFile.Data.ToDictionary(pair => pair.Key, pair => Encoding.UTF8.GetBytes(pair.Value))
            };

            try
            {
                await jobApi.CreateSecretAsync(secret, namespaceName, cancellationToken);
            }
            catch (Exception exception) when (IsAlreadyExistsConflict(exception))
            {
                // A retry can reuse the existing deterministic per-job secret.
            }
        }
    }
    private async Task DeleteSecretFilesAsync(string jobName, string namespaceName, CancellationToken cancellationToken)
    {
        try
        {
            await jobApi.DeleteSecretAsync(CodexAuthSecretName(jobName), namespaceName, cancellationToken);
            await jobApi.DeleteSecretAsync(ApiKeySecretName(jobName), namespaceName, cancellationToken);
        }
        catch (Exception exception) when (IsNotFound(exception))
        {
        }
    }

    public static string CodexAuthSecretName(string jobName)
        => $"{jobName}-codex-auth";

    public static string ApiKeySecretName(string jobName)
        => $"{jobName}-api-auth";
    private static V1ConfigMap? BuildContextConfigMap(KubernetesJobSpec spec, V1Job job)
    {
        if (spec.ContextFiles is not { Count: > 0 })
        {
            return null;
        }

        return new V1ConfigMap
        {
            ApiVersion = "v1",
            Kind = "ConfigMap",
            Metadata = new V1ObjectMeta
            {
                Name = ContextConfigMapName(spec.Name),
                Labels = new Dictionary<string, string>
                {
                    [ManagedByLabel] = ManagedByValue,
                    ["app.kubernetes.io/name"] = "formicae-agent-context",
                    ["formicae.hhnl.de/task"] = spec.Name
                },
                OwnerReferences = BuildOwnerReferences(job)
            },
            Data = spec.ContextFiles.ToDictionary(file => file.FileName, file => file.Content)
        };
    }

    private static IList<V1OwnerReference>? BuildOwnerReferences(V1Job job)
    {
        if (string.IsNullOrWhiteSpace(job.Metadata?.Name) || string.IsNullOrWhiteSpace(job.Metadata?.Uid))
        {
            return null;
        }

        return
        [
            new V1OwnerReference
            {
                ApiVersion = "batch/v1",
                Kind = "Job",
                Name = job.Metadata.Name,
                Uid = job.Metadata.Uid,
                Controller = false,
                BlockOwnerDeletion = false
            }
        ];
    }

    private static string ContextConfigMapName(string jobName)
        => $"{jobName}-context";

    private async Task DeleteContextConfigMapAsync(string jobName, string namespaceName, CancellationToken cancellationToken)
    {
        try
        {
            await jobApi.DeleteConfigMapAsync(ContextConfigMapName(jobName), namespaceName, cancellationToken);
        }
        catch (Exception exception) when (IsNotFound(exception))
        {
        }
    }

    private static bool IsAuthMethod(string? actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

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

    private static bool IsNotFound(Exception exception)
    {
        if (exception is KubernetesException kubernetesException && kubernetesException.Status.Code == 404)
        {
            return true;
        }

        return exception.Message.Contains("NotFound", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("status code 'NotFound'", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("\"code\":404", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAlreadyExistsConflict(Exception exception)
    {
        if (exception is KubernetesException kubernetesException && kubernetesException.Status.Code == 409)
        {
            return true;
        }

        return exception.Message.Contains("AlreadyExists", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("status code 'Conflict'", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("\"code\":409", StringComparison.OrdinalIgnoreCase);
    }

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
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                builder.AppendLine($"Unable to read logs: {exception.Message}");
            }
        }

        return builder.ToString();
    }

    private async Task DeleteIfConfiguredAsync(string jobName, string namespaceName, CancellationToken cancellationToken)
    {
        await DeleteSecretFilesAsync(jobName, namespaceName, cancellationToken);

        if (!options.Value.DeleteFinishedJobs)
        {
            return;
        }

        await jobApi.DeleteJobAsync(jobName, namespaceName, cancellationToken);
        await DeleteContextConfigMapAsync(jobName, namespaceName, cancellationToken);
    }

    private static string Combine(string manifest, string logs)
        => string.IsNullOrWhiteSpace(logs)
            ? manifest
            : $"{manifest}{Environment.NewLine}{logs}";
}
