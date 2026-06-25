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
    public string CodexSubscriptionBootstrapCommand { get; set; } = "apt-get update && apt-get install -y --no-install-recommends git ca-certificates && rm -rf /var/lib/apt/lists/*";
    public string CodexSubscriptionCommand { get; set; } = "mkdir -p \"$CODEX_HOME\" /workspace && if [ -f /root/.codex/auth.json ]; then cp /root/.codex/auth.json \"$CODEX_HOME/auth.json\" && chmod 600 \"$CODEX_HOME/auth.json\"; fi && if [ \"$FORMICAE_TASK_KIND\" = \"Implement\" ]; then repo=\"${FORMICAE_REPOSITORY_URL#https://}\" && git clone \"https://x-access-token:${GITHUB_TOKEN}@${repo}\" /workspace/repo && cd /workspace/repo && git checkout \"$FORMICAE_BRANCH\" && git config user.email \"formicae@example.invalid\" && git config user.name \"Formicae Agent\"; else cd /workspace; fi && codex_model_args=\"\" && if [ -n \"$FORMICAE_MODEL\" ]; then codex_model_args=\"-m $FORMICAE_MODEL\"; fi && npx -y @openai/codex exec $codex_model_args -C \"$PWD\" --skip-git-repo-check --json --dangerously-bypass-approvals-and-sandbox \"$FORMICAE_TASK_PROMPT\" && if [ \"$FORMICAE_TASK_KIND\" = \"Implement\" ]; then git add -A && if git diff --cached --quiet; then echo \"Codex completed without file changes.\"; else git commit -m \"Implement Formicae workflow ${FORMICAE_WORKFLOW_ID}\" && git push origin \"$FORMICAE_BRANCH\"; fi; fi";
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
        var rawName = $"formicae-{task.Kind.ToString().ToLowerInvariant()}-{task.WorkflowId:N}";
        var jobName = rawName[..Math.Min(63, rawName.Length)];
        var environment = BuildEnvironment(task, model, command.AuthMethod);
        var spec = new KubernetesJobSpec(
            jobName,
            command.Image ?? jobOptions.Value.Image,
            environment,
            [openHandsOptions.Value.Shell, "-lc", BuildShellCommand(command.BootstrapCommand, command.Command)],
            command.AuthMethod);

        var result = await jobRunner.RunJobAsync(spec, cancellationToken);
        return new AgentRunResult(result.Succeeded, result.JobName, result.Logs, result.FailureReason);
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
