using System.Security.Cryptography;
using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Kubernetes;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Infrastructure.OpenHands;

public sealed class OpenHandsAgentRunner : IAgentRunner
{
    private readonly IKubernetesJobRunner jobRunner;
    private readonly IOptions<KubernetesJobOptions> jobOptions;
    private readonly IOptions<OpenHandsOptions> openHandsOptions;
    private readonly AiSettingsService? aiSettingsService;

    public OpenHandsAgentRunner(
        IKubernetesJobRunner jobRunner,
        IOptions<KubernetesJobOptions> jobOptions,
        IOptions<OpenHandsOptions> openHandsOptions)
        : this(jobRunner, jobOptions, openHandsOptions, null)
    {
    }

    public OpenHandsAgentRunner(
        IKubernetesJobRunner jobRunner,
        IOptions<KubernetesJobOptions> jobOptions,
        IOptions<OpenHandsOptions> openHandsOptions,
        AiSettingsService? aiSettingsService)
    {
        this.jobRunner = jobRunner;
        this.jobOptions = jobOptions;
        this.openHandsOptions = openHandsOptions;
        this.aiSettingsService = aiSettingsService;
    }

    public async Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken cancellationToken)
    {
        var settings = aiSettingsService is null
            ? ResolveSettingsFromOptions(openHandsOptions.Value)
            : await aiSettingsService.ResolveAsync(cancellationToken);
        var spec = BuildSpec(task, settings);
        var start = await jobRunner.StartJobAsync(spec, cancellationToken);
        return new AgentRunStartResult(start.JobName);
    }

    public async Task<AgentRunResult?> TryGetResultAsync(string externalId, CancellationToken cancellationToken)
    {
        var result = await jobRunner.TryGetJobResultAsync(externalId, cancellationToken);
        if (result is null)
        {
            return null;
        }

        var output = result.Succeeded ? ExtractAgentOutput(result.Logs) : result.Logs;
        return new AgentRunResult(result.Succeeded, result.JobName, output, result.FailureReason);
    }

    private KubernetesJobSpec BuildSpec(AgentTask task, ResolvedAiSettings settings)
    {
        var command = ResolveCommand(openHandsOptions.Value, settings.AuthMethod);
        var model = ResolveModel(task, settings, command.AuthMethod);
        var jobName = BuildJobName(task);
        var environment = BuildEnvironment(task, model, settings.EndpointUrl, command.AuthMethod);
        return new KubernetesJobSpec(
            jobName,
            command.Image ?? jobOptions.Value.Image,
            environment,
            [openHandsOptions.Value.Shell, "-lc", BuildShellCommand(command.BootstrapCommand, command.Command)],
            command.AuthMethod,
            task.ContextFiles?.Select(file => new KubernetesJobContextFile(file.FileName, file.Content)).ToArray());
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
            if (!line.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!TryGetString(root, "type", out var eventType))
                {
                    continue;
                }

                if (string.Equals(eventType, "item.completed", StringComparison.OrdinalIgnoreCase)
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
                // Ignore non-agent JSON produced by bootstrap tools or package managers.
            }
        }

        var lastMessage = messages.LastOrDefault(message => !string.IsNullOrWhiteSpace(message));
        return string.IsNullOrWhiteSpace(lastMessage) ? logs : lastMessage;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static ResolvedAiSettings ResolveSettingsFromOptions(OpenHandsOptions options)
        => new(
            TrimToNull(options.Provider),
            TrimToNull(options.DefaultModel),
            TrimToNull(options.EndpointUrl),
            NormalizeAuthMethod(TrimToNull(options.AuthMethod) ?? OpenHandsAuthMethods.ApiKey),
            TrimToNull(options.LlmApiKeySecretName));

    private static string ResolveModel(AgentTask task, ResolvedAiSettings settings, string authMethod)
    {
        if (!string.IsNullOrWhiteSpace(task.Model))
        {
            return task.Model;
        }

        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            return settings.Model;
        }

        return IsAuthMethod(authMethod, OpenHandsAuthMethods.ApiKey)
            ? "openhands/claude-sonnet-4"
            : string.Empty;
    }

    private static Dictionary<string, string> BuildEnvironment(AgentTask task, string model, string? endpointUrl, string authMethod)
    {
        var environment = new Dictionary<string, string>
        {
            ["FORMICAE_WORKFLOW_ID"] = task.WorkflowId.ToString("N"),
            ["FORMICAE_TASK_KIND"] = task.Kind.ToString(),
            ["FORMICAE_REPOSITORY_URL"] = task.RepositoryUrl,
            ["FORMICAE_BRANCH"] = task.BranchName,
            ["FORMICAE_TASK_PROMPT"] = task.Prompt,
            ["FORMICAE_OPENHANDS_AUTH_METHOD"] = authMethod,
            ["FORMICAE_MODEL"] = model
        };

        if (IsAuthMethod(authMethod, OpenHandsAuthMethods.ApiKey))
        {
            environment["LLM_MODEL"] = model;
            if (!string.IsNullOrWhiteSpace(endpointUrl))
            {
                environment["LLM_BASE_URL"] = endpointUrl;
            }
        }
        else if (IsAuthMethod(authMethod, OpenHandsAuthMethods.CodexSubscription))
        {
            environment["CODEX_HOME"] = "/tmp/codex-home";
            if (!string.IsNullOrWhiteSpace(model))
            {
                environment["CODEX_CONFIG"] = $$"""{"model":"{{model}}"}""";
            }
        }

        return environment;
    }

    private static SelectedOpenHandsCommand ResolveCommand(OpenHandsOptions options, string authMethod)
    {
        if (IsAuthMethod(authMethod, OpenHandsAuthMethods.CodexSubscription))
        {
            return new SelectedOpenHandsCommand(
                KubernetesJobAuthMethods.CodexSubscription,
                options.CodexSubscriptionImage,
                options.CodexSubscriptionBootstrapCommand,
                options.CodexSubscriptionCommand);
        }

        if (IsAuthMethod(authMethod, OpenHandsAuthMethods.ApiKey))
        {
            return new SelectedOpenHandsCommand(
                KubernetesJobAuthMethods.ApiKey,
                null,
                options.BootstrapCommand,
                options.Command);
        }

        throw new InvalidOperationException($"Unsupported OpenHands auth method '{authMethod}'. Supported values are '{OpenHandsAuthMethods.ApiKey}' and '{OpenHandsAuthMethods.CodexSubscription}'.");
    }

    private static bool IsAuthMethod(string? actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAuthMethod(string authMethod)
    {
        if (IsAuthMethod(authMethod, OpenHandsAuthMethods.ApiKey))
        {
            return OpenHandsAuthMethods.ApiKey;
        }

        if (IsAuthMethod(authMethod, OpenHandsAuthMethods.CodexSubscription))
        {
            return OpenHandsAuthMethods.CodexSubscription;
        }

        return authMethod;
    }

    private static string? TrimToNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string BuildShellCommand(string bootstrapCommand, string command)
        => string.IsNullOrWhiteSpace(bootstrapCommand)
            ? command
            : $"{bootstrapCommand} && {command}";

    private sealed record SelectedOpenHandsCommand(string AuthMethod, string? Image, string BootstrapCommand, string Command);
}
