using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Kubernetes;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Infrastructure.OpenHands;

public sealed class OpenHandsOptions
{
    public string? DefaultModel { get; set; }
    public string LlmApiKeySecretName { get; set; } = "openhands-llm-api-key";
    public string Shell { get; set; } = "/bin/sh";
    public string BootstrapCommand { get; set; } = string.Empty;
    public string Command { get; set; } = "openhands --headless --json --override-with-envs -t \"$FORMICAE_TASK_PROMPT\"";
}

public sealed class OpenHandsAgentRunner(IKubernetesJobRunner jobRunner, IOptions<KubernetesJobOptions> jobOptions, IOptions<OpenHandsOptions> openHandsOptions) : IAgentRunner
{
    public async Task<AgentRunResult> RunAsync(AgentTask task, CancellationToken cancellationToken)
    {
        var model = task.Model ?? openHandsOptions.Value.DefaultModel ?? "openhands/claude-sonnet-4";
        var rawName = $"formicae-{task.Kind.ToString().ToLowerInvariant()}-{task.WorkflowId:N}";
        var jobName = rawName[..Math.Min(63, rawName.Length)];
        var spec = new KubernetesJobSpec(
            jobName,
            jobOptions.Value.Image,
            new Dictionary<string, string>
            {
                ["FORMICAE_WORKFLOW_ID"] = task.WorkflowId.ToString("N"),
                ["FORMICAE_TASK_KIND"] = task.Kind.ToString(),
                ["FORMICAE_REPOSITORY_URL"] = task.RepositoryUrl,
                ["FORMICAE_BRANCH"] = task.BranchName,
                ["FORMICAE_TASK_PROMPT"] = task.Prompt,
                ["LLM_MODEL"] = model
            },
            [openHandsOptions.Value.Shell, "-lc", BuildShellCommand(openHandsOptions.Value)]);

        var result = await jobRunner.RunJobAsync(spec, cancellationToken);
        return new AgentRunResult(result.Succeeded, result.JobName, result.Logs, result.FailureReason);
    }

    private static string BuildShellCommand(OpenHandsOptions options)
        => string.IsNullOrWhiteSpace(options.BootstrapCommand)
            ? options.Command
            : $"{options.BootstrapCommand} && {options.Command}";
}
