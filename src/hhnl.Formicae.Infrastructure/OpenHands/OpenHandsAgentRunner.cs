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
    public string CodexSubscriptionImage { get; set; } = "node:22-bookworm-slim";
    public string CodexSubscriptionBootstrapCommand { get; set; } = string.Empty;
    public string CodexSubscriptionCommand { get; set; } = "npx -y @agentclientprotocol/codex-acp";
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
        var model = task.Model ?? openHandsOptions.Value.DefaultModel ?? "openhands/claude-sonnet-4";
        var rawName = $"formicae-{task.Kind.ToString().ToLowerInvariant()}-{task.WorkflowId:N}";
        var jobName = rawName[..Math.Min(63, rawName.Length)];
        var command = ResolveCommand(openHandsOptions.Value);
        var environment = BuildEnvironment(task, model, command.AuthMethod);
        var spec = new KubernetesJobSpec(
            jobName,
            command.Image ?? jobOptions.Value.Image,
            environment,
            [openHandsOptions.Value.Shell, "-lc", BuildShellCommand(command.BootstrapCommand, command.Command)]);

        var result = await jobRunner.RunJobAsync(spec, cancellationToken);
        return new AgentRunResult(result.Succeeded, result.JobName, result.Logs, result.FailureReason);
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
            environment["CODEX_CONFIG"] = $$"""{"model":"{{model}}"}""";
        }

        return environment;
    }

    private static SelectedOpenHandsCommand ResolveCommand(OpenHandsOptions options)
    {
        if (IsAuthMethod(options.AuthMethod, OpenHandsAuthMethods.CodexSubscription))
        {
            return new SelectedOpenHandsCommand(
                OpenHandsAuthMethods.CodexSubscription,
                options.CodexSubscriptionImage,
                options.CodexSubscriptionBootstrapCommand,
                options.CodexSubscriptionCommand);
        }

        return new SelectedOpenHandsCommand(
            OpenHandsAuthMethods.ApiKey,
            null,
            options.BootstrapCommand,
            options.Command);
    }

    private static bool IsAuthMethod(string? actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static string BuildShellCommand(string bootstrapCommand, string command)
        => string.IsNullOrWhiteSpace(bootstrapCommand)
            ? command
            : $"{bootstrapCommand} && {command}";

    private sealed record SelectedOpenHandsCommand(string AuthMethod, string? Image, string BootstrapCommand, string Command);
}
