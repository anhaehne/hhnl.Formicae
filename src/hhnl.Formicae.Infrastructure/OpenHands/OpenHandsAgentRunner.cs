using System.Security.Cryptography;
using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Kubernetes;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Infrastructure.OpenHands;

public sealed class OpenHandsOptions
{
    public string AuthMethod { get; set; } = OpenHandsAuthMethods.ApiKey;
    public string? DefaultModel { get; set; }
    public string LlmApiKeySecretName { get; set; } = "openhands-llm-api-key";
    public string Shell { get; set; } = "/bin/sh";
    public string BootstrapCommand { get; set; } = string.Empty;
    public string Command { get; set; } = "openhands --headless --json --override-with-envs -t \"$FORMICAE_TASK_PROMPT\"";
    public string CodexSubscriptionImage { get; set; } = "mcr.microsoft.com/dotnet/sdk:10.0";
    public string CodexSubscriptionBootstrapCommand { get; set; } = "apt-get update && apt-get install -y --no-install-recommends git ca-certificates curl gnupg && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && apt-get install -y --no-install-recommends nodejs && rm -rf /var/lib/apt/lists/*";
    public string CodexSubscriptionCommand { get; set; } = "mkdir -p \"$CODEX_HOME\" /workspace && if [ -f /root/.codex/auth.json ]; then cp /root/.codex/auth.json \"$CODEX_HOME/auth.json\" && chmod 600 \"$CODEX_HOME/auth.json\"; fi && repo=\"${FORMICAE_REPOSITORY_URL#https://}\" && if [ \"$FORMICAE_TASK_KIND\" = \"Implement\" ] || [ \"$FORMICAE_TASK_KIND\" = \"AddressComments\" ]; then if [ -z \"$GITHUB_TOKEN\" ]; then echo \"GITHUB_TOKEN is required to push workflow changes.\" >&2; exit 1; fi; git clone \"https://x-access-token:${GITHUB_TOKEN}@${repo}\" /workspace/repo && cd /workspace/repo && git checkout \"$FORMICAE_BRANCH\" && git remote set-url origin \"https://x-access-token:${GITHUB_TOKEN}@${repo}\" && git config user.email \"formicae@example.invalid\" && git config user.name \"Formicae Agent\"; else cd /workspace; fi && codex_model_args=\"\" && if [ -n \"$FORMICAE_MODEL\" ]; then codex_model_args=\"-m $FORMICAE_MODEL\"; fi && npx -y @openai/codex exec $codex_model_args -C \"$PWD\" --skip-git-repo-check --json --dangerously-bypass-approvals-and-sandbox \"$FORMICAE_TASK_PROMPT\" && if [ \"$FORMICAE_TASK_KIND\" = \"Implement\" ] || [ \"$FORMICAE_TASK_KIND\" = \"AddressComments\" ]; then git remote set-url origin \"https://x-access-token:${GITHUB_TOKEN}@${repo}\" && git add -A && if git diff --cached --quiet; then echo \"Codex completed without uncommitted file changes.\"; else commit_subject=\"Implement Formicae workflow ${FORMICAE_WORKFLOW_ID}\" && if [ \"$FORMICAE_TASK_KIND\" = \"AddressComments\" ]; then commit_subject=\"Address comments for Formicae workflow ${FORMICAE_WORKFLOW_ID}\"; fi && git commit -m \"$commit_subject\"; fi && git push origin \"$FORMICAE_BRANCH\"; fi";
}

public static class OpenHandsAuthMethods
{
    public const string ApiKey = "ApiKey";
    public const string CodexSubscription = "CodexSubscription";
}

public sealed class OpenHandsAgentRunner(IKubernetesJobRunner jobRunner, IOptions<KubernetesJobOptions> jobOptions, IOptions<OpenHandsOptions> openHandsOptions) : IAgentRunner
{
    public async Task<AgentRunResult> RunAsync(AgentTask task, CancellationToken cancellationToken)
    {
        var command = ResolveCommand(openHandsOptions.Value);
        var model = ResolveModel(task, openHandsOptions.Value, command.AuthMethod);
        var jobName = BuildJobName(task);
        var environment = BuildEnvironment(task, model, command.AuthMethod);
        var spec = new KubernetesJobSpec(
            jobName,
            command.Image ?? jobOptions.Value.Image,
            environment,
            [openHandsOptions.Value.Shell, "-lc", BuildShellCommand(command.BootstrapCommand, command.Command)],
            command.AuthMethod);

        var result = await jobRunner.RunJobAsync(spec, cancellationToken);
        var output = result.Succeeded ? ExtractAgentOutput(result.Logs) : result.Logs;
        return new AgentRunResult(result.Succeeded, result.JobName, output, result.FailureReason);
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

    private static string ResolveModel(AgentTask task, OpenHandsOptions options, string authMethod)
    {
        if (!string.IsNullOrWhiteSpace(task.Model))
        {
            return task.Model;
        }

        if (!string.IsNullOrWhiteSpace(options.DefaultModel))
        {
            return options.DefaultModel;
        }

        return IsAuthMethod(authMethod, OpenHandsAuthMethods.ApiKey)
            ? "openhands/claude-sonnet-4"
            : string.Empty;
    }

    private static Dictionary<string, string> BuildEnvironment(AgentTask task, string model, string authMethod)
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

    private static SelectedOpenHandsCommand ResolveCommand(OpenHandsOptions options)
    {
        if (IsAuthMethod(options.AuthMethod, OpenHandsAuthMethods.CodexSubscription))
        {
            return new SelectedOpenHandsCommand(
                KubernetesJobAuthMethods.CodexSubscription,
                options.CodexSubscriptionImage,
                options.CodexSubscriptionBootstrapCommand,
                options.CodexSubscriptionCommand);
        }

        if (IsAuthMethod(options.AuthMethod, OpenHandsAuthMethods.ApiKey))
        {
            return new SelectedOpenHandsCommand(
                KubernetesJobAuthMethods.ApiKey,
                null,
                options.BootstrapCommand,
                options.Command);
        }

        throw new InvalidOperationException($"Unsupported OpenHands auth method '{options.AuthMethod}'. Supported values are '{OpenHandsAuthMethods.ApiKey}' and '{OpenHandsAuthMethods.CodexSubscription}'.");
    }

    private static bool IsAuthMethod(string? actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static string BuildShellCommand(string bootstrapCommand, string command)
        => string.IsNullOrWhiteSpace(bootstrapCommand)
            ? command
            : $"{bootstrapCommand} && {command}";

    private sealed record SelectedOpenHandsCommand(string AuthMethod, string? Image, string BootstrapCommand, string Command);
}
