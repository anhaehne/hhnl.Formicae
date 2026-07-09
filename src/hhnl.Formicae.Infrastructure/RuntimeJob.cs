namespace hhnl.Formicae.Infrastructure;

public interface IJobRuntime
{
    Task<RuntimeJobStartResult> StartJobAsync(RuntimeJobSpec spec, CancellationToken cancellationToken);

    Task<RuntimeJobResult?> TryGetJobResultAsync(string externalId, CancellationToken cancellationToken);

    Task<string> ReadJobLogsAsync(string externalId, CancellationToken cancellationToken);
}

public sealed record RuntimeJobSpec(
    string Name,
    string Image,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> Command,
    string AuthMethod = RuntimeJobAuthMethods.ApiKey,
    IReadOnlyList<RuntimeJobContextFile>? ContextFiles = null,
    string ContextFilesMountPath = "/workspace/formicae/context",
    IReadOnlyList<RuntimeJobSecretFile>? SecretFiles = null,
    RuntimeJobSecretEnvironment? SecretEnvironment = null);

public sealed record RuntimeJobContextFile(string FileName, string Content);

public sealed record RuntimeJobSecretFile(string SecretName, string MountPath, IReadOnlyDictionary<string, string> Data);

public sealed record RuntimeJobSecretEnvironment(string SecretName, IReadOnlyDictionary<string, string> Data);

public sealed record RuntimeJobStartResult(string ExternalId);

public sealed record RuntimeJobResult(bool Succeeded, string ExternalId, string Logs, string? FailureReason);

public static class RuntimeJobAuthMethods
{
    public const string None = "None";
    public const string ApiKey = "ApiKey";
    public const string CodexSubscription = "CodexSubscription";
}

public sealed class RuntimeJobOptions
{
    public string Image { get; set; } = "docker.io/limeray/hhnl-formicae-worker:latest";
    public string WorkerCallbackUrl { get; set; } = string.Empty;
    public string WorkerCallbackSecret { get; set; } = string.Empty;
    public string CodexAuthSecretKey { get; set; } = "auth.json";
    public string CodexAuthMountPath { get; set; } = "/root/.codex";
}
