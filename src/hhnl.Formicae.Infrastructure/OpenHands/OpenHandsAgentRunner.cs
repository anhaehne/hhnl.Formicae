using System.Security.Cryptography;
using System.Text.Json;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Kubernetes;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Infrastructure.OpenHands;

public sealed class OpenHandsAgentRunner : IAgentRunner
{
    private static readonly IReadOnlyList<string> WorkerCommand = ["dotnet", "hhnl.Formicae.Worker.dll"];

    private readonly IKubernetesJobRunner jobRunner;
    private readonly IOptions<KubernetesJobOptions> jobOptions;
    private readonly IOptions<OpenHandsOptions> openHandsOptions;
    private readonly AiSettingsService? aiSettingsService;
    private readonly IDevOpsIntegrationStore? integrationStore;
    private readonly IGitHubAppClient? gitHubAppClient;

    public OpenHandsAgentRunner(IKubernetesJobRunner jobRunner, IOptions<KubernetesJobOptions> jobOptions, IOptions<OpenHandsOptions> openHandsOptions)
        : this(jobRunner, jobOptions, openHandsOptions, null)
    {
    }

    public OpenHandsAgentRunner(IKubernetesJobRunner jobRunner, IOptions<KubernetesJobOptions> jobOptions, IOptions<OpenHandsOptions> openHandsOptions, AiSettingsService? aiSettingsService, IDevOpsIntegrationStore? integrationStore = null, IGitHubAppClient? gitHubAppClient = null)
    {
        this.jobRunner = jobRunner;
        this.jobOptions = jobOptions;
        this.openHandsOptions = openHandsOptions;
        this.aiSettingsService = aiSettingsService;
        this.integrationStore = integrationStore;
        this.gitHubAppClient = gitHubAppClient;
    }

    public async Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken cancellationToken)
    {
        var settings = aiSettingsService is null ? ResolveSettingsFromOptions(openHandsOptions.Value) : await aiSettingsService.ResolveAsync(cancellationToken);
        var gitAccessToken = await CreateGitAccessTokenAsync(task, cancellationToken);
        var spec = BuildSpec(task, settings, gitAccessToken);
        var start = await jobRunner.StartJobAsync(spec, cancellationToken);
        return new AgentRunStartResult(start.JobName);
    }

    public async Task<AgentRunResult?> TryGetResultAsync(string externalId, CancellationToken cancellationToken)
    {
        var result = await jobRunner.TryGetJobResultAsync(externalId, cancellationToken);
        if (result is null) return null;
        var output = result.Succeeded ? ExtractAgentOutput(result.Logs) : result.Logs;
        return new AgentRunResult(result.Succeeded, result.JobName, output, result.FailureReason);
    }

    private KubernetesJobSpec BuildSpec(AgentTask task, ResolvedAiSettings settings, string? gitAccessToken)
    {
        var authMethod = ResolveAuthMethod(settings.AuthMethod);
        var model = ResolveModel(task, settings, authMethod);
        var jobName = BuildJobName(task);
        var environment = BuildEnvironment(task, jobName, model, settings, authMethod, jobOptions.Value, gitAccessToken);
        var secretFiles = BuildSecretFiles(jobName, settings, authMethod, jobOptions.Value);
        return new KubernetesJobSpec(jobName, jobOptions.Value.Image, environment, WorkerCommand, ToKubernetesAuthMethod(authMethod), task.ContextFiles?.Select(file => new KubernetesJobContextFile(file.FileName, file.Content)).ToArray(), SecretFiles: secretFiles);
    }

    private async Task<string?> CreateGitAccessTokenAsync(AgentTask task, CancellationToken cancellationToken)
    {
        if (task.Kind is not (TaskRunKind.Implement or TaskRunKind.AddressComments) || integrationStore is null || gitHubAppClient is null) return null;
        var connectedRepository = await integrationStore.GetRepositoryByUrlAsync(task.RepositoryUrl, cancellationToken);
        if (connectedRepository?.InstallationId is not { } installationId) return null;
        var integration = connectedRepository.DevOpsIntegration ?? await integrationStore.GetAsync(connectedRepository.DevOpsIntegrationId, cancellationToken);
        return integration is null ? null : await gitHubAppClient.CreateInstallationTokenAsync(integration, installationId, cancellationToken);
    }

    private static string BuildJobName(AgentTask task)
    {
        var prefix = $"formicae-{task.Kind.ToString().ToLowerInvariant()}-{task.WorkflowId:N}";
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(task.Prompt)))[..8].ToLowerInvariant();
        var suffix = $"-{hash}";
        var maxPrefixLength = 63 - suffix.Length;
        return $"{prefix[..Math.Min(prefix.Length, maxPrefixLength)]}{suffix}";
    }

    private static string ExtractAgentOutput(string logs)
    {
        var messages = new List<string>();
        foreach (var line in logs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (TryGetString(root, "type", out var eventType)
                    && string.Equals(eventType, "item.completed", StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("item", out var item)
                    && TryGetString(item, "type", out var itemType)
                    && string.Equals(itemType, "agent_message", StringComparison.OrdinalIgnoreCase)
                    && TryGetString(item, "text", out var text))
                {
                    messages.Add(text);
                }
            }
            catch (JsonException)
            {
            }
        }

        var lastMessage = messages.LastOrDefault(message => !string.IsNullOrWhiteSpace(message));
        return string.IsNullOrWhiteSpace(lastMessage) ? logs : lastMessage;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static ResolvedAiSettings ResolveSettingsFromOptions(OpenHandsOptions options)
        => new(
            AiSettings.DefaultId,
            AiSettings.DefaultName,
            TrimToNull(options.Provider),
            TrimToNull(options.DefaultModel),
            TrimToNull(options.EndpointUrl),
            AgentKinds.OpenHands,
            null,
            null,
            NormalizeAuthMethod(TrimToNull(options.AuthMethod) ?? OpenHandsAuthMethods.ApiKey),
            TrimToNull(options.LlmApiKeySecretName),
            null,
            "LLM_API_KEY",
            null,
            "auth.json",
            "/root/.codex",
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private static string ResolveModel(AgentTask task, ResolvedAiSettings settings, string authMethod)
    {
        if (!string.IsNullOrWhiteSpace(task.Model)) return task.Model;
        if (!string.IsNullOrWhiteSpace(settings.Model)) return settings.Model;
        return IsApiKeyAuth(authMethod) ? "openhands/claude-sonnet-4" : string.Empty;
    }

    private static Dictionary<string, string> BuildEnvironment(AgentTask task, string jobName, string model, ResolvedAiSettings settings, string authMethod, KubernetesJobOptions options, string? gitAccessToken)
    {
        var environment = new Dictionary<string, string>
        {
            ["FORMICAE_WORKFLOW_ID"] = task.WorkflowId.ToString("N"),
            ["FORMICAE_TASK_KIND"] = task.Kind.ToString(),
            ["FORMICAE_REPOSITORY_URL"] = task.RepositoryUrl,
            ["FORMICAE_BRANCH"] = task.BranchName,
            ["FORMICAE_TASK_PROMPT"] = task.Prompt,
            ["FORMICAE_OPENHANDS_AUTH_METHOD"] = authMethod,
            ["FORMICAE_MODEL"] = model,
            ["FORMICAE_EXTERNAL_ID"] = jobName,
            ["FORMICAE_CONTEXT_PATH"] = "/workspace/formicae/context"
        };

        if (!string.IsNullOrWhiteSpace(gitAccessToken)) environment["FORMICAE_GIT_ACCESS_TOKEN"] = gitAccessToken;
        if (!string.IsNullOrWhiteSpace(options.WorkerCallbackUrl)) environment["FORMICAE_WORKER_CALLBACK_URL"] = options.WorkerCallbackUrl;
        if (!string.IsNullOrWhiteSpace(options.WorkerCallbackSecret)) environment["FORMICAE_WORKER_CALLBACK_SECRET"] = options.WorkerCallbackSecret;

        if (IsApiKeyAuth(authMethod))
        {
            environment["LLM_MODEL"] = model;
            if (!string.IsNullOrWhiteSpace(settings.EndpointUrl)) environment["LLM_BASE_URL"] = settings.EndpointUrl;
            if (!string.IsNullOrWhiteSpace(settings.LlmApiKey) && !string.IsNullOrWhiteSpace(settings.ApiKeyEnvironmentVariable)) environment[settings.ApiKeyEnvironmentVariable] = settings.LlmApiKey;
        }
        else if (IsAuthMethod(authMethod, OpenHandsAuthMethods.CodexSubscription))
        {
            environment["CODEX_HOME"] = settings.SubscriptionCredentialMountPath ?? "/root/.codex";
            if (!string.IsNullOrWhiteSpace(model)) environment["CODEX_CONFIG"] = $"{{\"model\":\"{model}\"}}";
        }

        if (string.Equals(settings.AgentKind, AgentKinds.Acp, StringComparison.OrdinalIgnoreCase))
        {
            environment["FORMICAE_AGENT_KIND"] = AgentKinds.Acp;
            if (!string.IsNullOrWhiteSpace(settings.AcpProvider)) environment["FORMICAE_ACP_PROVIDER"] = settings.AcpProvider;
            if (!string.IsNullOrWhiteSpace(settings.AcpCommand)) environment["FORMICAE_ACP_COMMAND"] = settings.AcpCommand;
        }

        return environment;
    }

    private static IReadOnlyList<KubernetesJobSecretFile>? BuildSecretFiles(string jobName, ResolvedAiSettings settings, string authMethod, KubernetesJobOptions options)
    {
        var credentialJson = settings.SubscriptionCredentialJson ?? settings.CodexAuthJson;
        if (!IsAuthMethod(authMethod, OpenHandsAuthMethods.CodexSubscription) || string.IsNullOrWhiteSpace(credentialJson)) return null;
        var fileName = settings.SubscriptionCredentialFileName ?? options.CodexAuthSecretKey;
        var mountPath = settings.SubscriptionCredentialMountPath ?? options.CodexAuthMountPath;
        return [new KubernetesJobSecretFile(KubernetesJobRunner.CodexAuthSecretName(jobName), mountPath, new Dictionary<string, string> { [fileName] = credentialJson })];
    }

    private static KubernetesJobSecretEnvironment? BuildSecretEnvironment(string jobName, ResolvedAiSettings settings, string authMethod)
    {
        if (!IsApiKeyAuth(authMethod) || string.IsNullOrWhiteSpace(settings.LlmApiKey) || string.IsNullOrWhiteSpace(settings.ApiKeyEnvironmentVariable))
        {
            return null;
        }

        return new KubernetesJobSecretEnvironment(
            KubernetesJobRunner.ApiKeySecretName(jobName),
            new Dictionary<string, string> { [settings.ApiKeyEnvironmentVariable] = settings.LlmApiKey });
    }
    private static string ResolveAuthMethod(string authMethod)
    {
        if (IsAuthMethod(authMethod, OpenHandsAuthMethods.CodexSubscription)) return OpenHandsAuthMethods.CodexSubscription;
        if (IsAuthMethod(authMethod, OpenHandsAuthMethods.ApiKey)) return OpenHandsAuthMethods.ApiKey;
        if (IsAuthMethod(authMethod, OpenHandsAuthMethods.OpenHandsCloud)) return OpenHandsAuthMethods.OpenHandsCloud;
        throw new InvalidOperationException($"Unsupported OpenHands auth method '{authMethod}'. Supported values are '{OpenHandsAuthMethods.ApiKey}', '{OpenHandsAuthMethods.OpenHandsCloud}' and '{OpenHandsAuthMethods.CodexSubscription}'.");
    }

    private static string ToKubernetesAuthMethod(string authMethod)
        => IsAuthMethod(authMethod, OpenHandsAuthMethods.CodexSubscription) ? KubernetesJobAuthMethods.CodexSubscription : KubernetesJobAuthMethods.ApiKey;

    private static bool IsApiKeyAuth(string authMethod)
        => IsAuthMethod(authMethod, OpenHandsAuthMethods.ApiKey) || IsAuthMethod(authMethod, OpenHandsAuthMethods.OpenHandsCloud);

    private static bool IsAuthMethod(string? actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAuthMethod(string authMethod)
    {
        if (IsAuthMethod(authMethod, OpenHandsAuthMethods.ApiKey)) return OpenHandsAuthMethods.ApiKey;
        if (IsAuthMethod(authMethod, OpenHandsAuthMethods.OpenHandsCloud)) return OpenHandsAuthMethods.OpenHandsCloud;
        if (IsAuthMethod(authMethod, OpenHandsAuthMethods.CodexSubscription)) return OpenHandsAuthMethods.CodexSubscription;
        return authMethod;
    }

    private static string? TrimToNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
