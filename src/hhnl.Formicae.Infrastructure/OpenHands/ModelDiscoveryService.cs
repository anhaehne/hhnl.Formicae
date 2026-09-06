using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using hhnl.Formicae.Application.Workflows;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Infrastructure.OpenHands;

public sealed record DiscoveredModel(string Id, string DisplayName, bool IsDefault);
public sealed record ModelDiscoveryStatus(string AiSettingsId, string? JobName, string Status, IReadOnlyList<DiscoveredModel> Models, string? FailureReason = null);

public sealed class ModelDiscoveryService(IJobRuntime runtime, IOptions<RuntimeJobOptions> options, AiSettingsService settingsService)
{
    public async Task<ModelDiscoveryStatus> StartAsync(string settingsId, CancellationToken cancellationToken)
    {
        var settings = await settingsService.ResolveAsync(settingsId, cancellationToken)
            ?? throw new ArgumentException("AI configuration was not found.");
        if (!OpenHandsAgentRunner.UsesCodexCli(settings))
            return new(settingsId, null, "Unsupported", [], "CLI model discovery currently supports Codex subscription execution only.");
        var name = Prefix(settingsId) + Guid.NewGuid().ToString("N")[..12];
        var env = new Dictionary<string, string>
        {
            ["FORMICAE_WORKFLOW_ID"] = Guid.Empty.ToString(),
            ["FORMICAE_TASK_KIND"] = "ModelDiscovery",
            ["FORMICAE_REPOSITORY_URL"] = "https://example.invalid/discovery",
            ["FORMICAE_BRANCH"] = "unused",
            ["FORMICAE_TASK_PROMPT"] = "Discover CLI models",
            ["FORMICAE_OPENHANDS_AUTH_METHOD"] = settings.AuthMethod,
            ["FORMICAE_AI_SETTINGS_ID"] = settings.Id,
            ["FORMICAE_EXTERNAL_ID"] = name,
            ["CODEX_HOME"] = "/tmp/codex-home",
            ["FORMICAE_CODEX_AUTH_MOUNT_PATH"] = settings.SubscriptionCredentialMountPath ?? options.Value.CodexAuthMountPath,
            ["FORMICAE_CODEX_AUTH_FILE_NAME"] = settings.SubscriptionCredentialFileName ?? options.Value.CodexAuthSecretKey
        };
        if (!string.IsNullOrWhiteSpace(options.Value.WorkerCallbackUrl)) env["FORMICAE_WORKER_CALLBACK_URL"] = options.Value.WorkerCallbackUrl;
        if (!string.IsNullOrWhiteSpace(options.Value.WorkerCallbackSecret)) env["FORMICAE_WORKER_CALLBACK_SECRET"] = options.Value.WorkerCallbackSecret;
        var result = await runtime.StartJobAsync(new RuntimeJobSpec(name, options.Value.Image, env,
            ["dotnet", "hhnl.Formicae.Worker.dll"], RuntimeJobAuthMethods.CodexSubscription,
            SecretFiles: OpenHandsAgentRunner.BuildSecretFiles(name, settings, settings.AuthMethod, options.Value),
            ExecutionPolicy: new RuntimeJobExecutionPolicy(120, 0)), cancellationToken);
        return new(settingsId, result.ExternalId, "Running", []);
    }

    public async Task<ModelDiscoveryStatus> GetStatusAsync(string settingsId, string jobName, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(jobName, "^" + Regex.Escape(Prefix(settingsId)) + "[a-f0-9]{12}$"))
            throw new ArgumentException("Discovery job does not belong to this AI configuration.");
        var result = await runtime.TryGetJobResultAsync(jobName, cancellationToken);
        if (result is null) return new(settingsId, jobName, "Running", []);
        if (!result.Succeeded) return new(settingsId, jobName, "Failed", [], "CLI discovery failed or timed out. Check the selected configuration's authentication and retry.");
        try
        {
            foreach (var line in result.Logs.Split('\n').Reverse())
            {
                if (!line.StartsWith("{\"type\":\"formicae.models\"", StringComparison.Ordinal)) continue;
                if (line.Length > 1024 * 1024) break;
                using var document = JsonDocument.Parse(line);
                var models = document.RootElement.GetProperty("models").Deserialize<DiscoveredModel[]>(JsonSerializerOptions.Web);
                if (models is null || models.Length > 2000 || models.Any(m => m is null || string.IsNullOrWhiteSpace(m.Id) || m.Id.Length > 256 || m.DisplayName is null || m.DisplayName.Length > 256)) break;
                return new(settingsId, jobName, "Succeeded", models);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException) { }
        return new(settingsId, jobName, "Failed", [], "CLI discovery returned an invalid model catalog.");
    }

    private static string Prefix(string settingsId) => "formicae-models-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settingsId)))[..12].ToLowerInvariant() + "-";
}
