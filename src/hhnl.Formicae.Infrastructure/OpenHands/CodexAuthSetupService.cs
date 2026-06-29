using System.Security.Cryptography;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Kubernetes;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Infrastructure.OpenHands;

public sealed record CodexAuthSetupStartResponse(string AiSettingsId, string JobName, string Status, string Output, string? FailureReason);

public sealed record CodexAuthSetupStatusResponse(string AiSettingsId, string JobName, string Status, string Output, string? FailureReason);

public sealed class CodexAuthSetupService(
    IKubernetesJobRunner jobRunner,
    IOptions<KubernetesJobOptions> jobOptions,
    IOptions<OpenHandsOptions> openHandsOptions,
    AiSettingsService aiSettingsService)
{
    private static readonly IReadOnlyList<string> WorkerCommand = ["dotnet", "hhnl.Formicae.Worker.dll"];

    public async Task<CodexAuthSetupStartResponse> StartAsync(string aiSettingsId, CancellationToken cancellationToken)
    {
        var settings = await aiSettingsService.ResolveAsync(aiSettingsId, cancellationToken)
            ?? throw new ArgumentException($"AI settings '{aiSettingsId}' were not found.");
        if (!string.Equals(settings.AuthMethod, OpenHandsAuthMethods.CodexSubscription, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Codex subscription login can only be started for subscription credentials.");
        }

        var jobName = BuildJobName(aiSettingsId);
        var environment = new Dictionary<string, string>
        {
            ["FORMICAE_WORKFLOW_ID"] = Guid.Empty.ToString("N"),
            ["FORMICAE_TASK_KIND"] = "CodexAuthSetup",
            ["FORMICAE_REPOSITORY_URL"] = "https://example.invalid/formicae/auth-setup",
            ["FORMICAE_BRANCH"] = "main",
            ["FORMICAE_TASK_PROMPT"] = "Connect Codex subscription credentials.",
            ["FORMICAE_OPENHANDS_AUTH_METHOD"] = "CodexSubscriptionSetup",
            ["FORMICAE_EXTERNAL_ID"] = jobName,
            ["FORMICAE_AI_SETTINGS_ID"] = settings.Id,
            ["CODEX_HOME"] = "/tmp/codex-home",
            ["FORMICAE_CODEX_LOGIN_COMMAND"] = ResolveLoginCommand()
        };

        if (!string.IsNullOrWhiteSpace(jobOptions.Value.WorkerCallbackUrl)) environment["FORMICAE_WORKER_CALLBACK_URL"] = jobOptions.Value.WorkerCallbackUrl;
        if (!string.IsNullOrWhiteSpace(jobOptions.Value.WorkerCallbackSecret)) environment["FORMICAE_WORKER_CALLBACK_SECRET"] = jobOptions.Value.WorkerCallbackSecret;

        var spec = new KubernetesJobSpec(jobName, jobOptions.Value.Image, environment, WorkerCommand, KubernetesJobAuthMethods.None);
        var start = await jobRunner.StartJobAsync(spec, cancellationToken);
        return new CodexAuthSetupStartResponse(settings.Id, start.JobName, "Running", string.Empty, null);
    }

    public async Task<CodexAuthSetupStatusResponse> GetStatusAsync(string aiSettingsId, string jobName, CancellationToken cancellationToken)
    {
        var result = await jobRunner.TryGetJobResultAsync(jobName, cancellationToken);
        if (result is null)
        {
            var logs = await jobRunner.ReadJobLogsAsync(jobName, cancellationToken);
            return new CodexAuthSetupStatusResponse(aiSettingsId, jobName, "Running", logs, null);
        }

        return new CodexAuthSetupStatusResponse(
            aiSettingsId,
            jobName,
            result.Succeeded ? "Succeeded" : "Failed",
            result.Logs,
            result.FailureReason);
    }

    private string ResolveLoginCommand()
        => string.IsNullOrWhiteSpace(openHandsOptions.Value.CodexSubscriptionLoginCommand)
            ? "npx -y @openai/codex login"
            : openHandsOptions.Value.CodexSubscriptionLoginCommand;

    private static string BuildJobName(string aiSettingsId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(aiSettingsId)))[..8].ToLowerInvariant();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"formicae-codex-login-{hash}-{suffix}";
    }
}
