using System.Security.Cryptography;
using System.Text.RegularExpressions;
using hhnl.Formicae.Application.Workflows;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Infrastructure.OpenHands;

public sealed record CodexAuthSetupStartResponse(string AiSettingsId, string JobName, string Status, string Output, string? FailureReason, string? DeviceLoginUrl, string? DeviceLoginCode);

public sealed record CodexAuthSetupStatusResponse(string AiSettingsId, string JobName, string Status, string Output, string? FailureReason, string? DeviceLoginUrl, string? DeviceLoginCode);

public sealed class CodexAuthSetupService(
    IJobRuntime jobRuntime,
    IOptions<RuntimeJobOptions> jobOptions,
    IOptions<OpenHandsOptions> openHandsOptions,
    AiSettingsService aiSettingsService)
{
    private static readonly IReadOnlyList<string> WorkerCommand = ["dotnet", "hhnl.Formicae.Worker.dll"];
    private static readonly Regex AnsiEscapeRegex = new("\u001b\\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex DeviceLoginUrlRegex = new(@"https://auth\.openai\.com/codex/device", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DeviceLoginCodeRegex = new(@"\b[A-Z0-9]{4}-[A-Z0-9]{5}\b", RegexOptions.Compiled);

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
            ["NO_COLOR"] = "1",
            ["TERM"] = "dumb",
            ["FORMICAE_CODEX_LOGIN_COMMAND"] = ResolveLoginCommand()
        };

        if (!string.IsNullOrWhiteSpace(jobOptions.Value.WorkerCallbackUrl)) environment["FORMICAE_WORKER_CALLBACK_URL"] = jobOptions.Value.WorkerCallbackUrl;
        if (!string.IsNullOrWhiteSpace(jobOptions.Value.WorkerCallbackSecret)) environment["FORMICAE_WORKER_CALLBACK_SECRET"] = jobOptions.Value.WorkerCallbackSecret;

        var spec = new RuntimeJobSpec(jobName, jobOptions.Value.Image, environment, WorkerCommand, RuntimeJobAuthMethods.None);
        var start = await jobRuntime.StartJobAsync(spec, cancellationToken);
        return new CodexAuthSetupStartResponse(settings.Id, start.ExternalId, "Running", string.Empty, null, null, null);
    }

    public async Task<CodexAuthSetupStatusResponse> GetStatusAsync(string aiSettingsId, string jobName, CancellationToken cancellationToken)
    {
        var result = await jobRuntime.TryGetJobResultAsync(jobName, cancellationToken);
        if (result is null)
        {
            var logs = await jobRuntime.ReadJobLogsAsync(jobName, cancellationToken);
            var output = CleanOutput(logs);
            return new CodexAuthSetupStatusResponse(aiSettingsId, jobName, "Running", output, null, ExtractDeviceLoginUrl(output), ExtractDeviceLoginCode(output));
        }

        var resultOutput = CleanOutput(result.Logs);
        return new CodexAuthSetupStatusResponse(
            aiSettingsId,
            jobName,
            result.Succeeded ? "Succeeded" : "Failed",
            resultOutput,
            result.FailureReason,
            ExtractDeviceLoginUrl(resultOutput),
            ExtractDeviceLoginCode(resultOutput));
    }

    private static string CleanOutput(string output)
    {
        var withoutAnsi = AnsiEscapeRegex.Replace(output, string.Empty);
        return new string(withoutAnsi.Where(character => character is '\r' or '\n' or '\t' || !char.IsControl(character)).ToArray());
    }

    private static string? ExtractDeviceLoginUrl(string output)
        => DeviceLoginUrlRegex.Match(output) is { Success: true } match ? match.Value : null;

    private static string? ExtractDeviceLoginCode(string output)
        => DeviceLoginCodeRegex.Match(output) is { Success: true } match ? match.Value : null;

    private string ResolveLoginCommand()
        => string.IsNullOrWhiteSpace(openHandsOptions.Value.CodexSubscriptionLoginCommand)
            ? "npx -y @openai/codex login --device-auth"
            : openHandsOptions.Value.CodexSubscriptionLoginCommand;

    private static string BuildJobName(string aiSettingsId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(aiSettingsId)))[..8].ToLowerInvariant();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"formicae-codex-login-{hash}-{suffix}";
    }
}
