using System.Text.Json;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Application.Workflows;

public sealed class AiSettingsService(
    IAiSettingsStore store,
    IOptions<OpenHandsOptions> openHandsOptions,
    IClock clock)
{
    public async Task<AiSettingsResponse> GetAsync(CancellationToken cancellationToken)
    {
        var settings = await ResolveAsync(cancellationToken);
        return ToResponse(settings);
    }

    public async Task<AiSettingsResponse> UpdateAsync(UpdateAiSettingsRequest request, CancellationToken cancellationToken)
    {
        var existing = await store.GetAsync(cancellationToken);
        var normalized = Normalize(request, existing);
        var settings = new AiSettings
        {
            Id = AiSettings.DefaultId,
            Provider = normalized.Provider,
            Model = normalized.Model,
            EndpointUrl = normalized.EndpointUrl,
            AgentKind = normalized.AgentKind,
            AcpProvider = normalized.AcpProvider,
            AcpCommand = normalized.AcpCommand,
            AuthMethod = normalized.AuthMethod,
            LlmApiKeySecretName = normalized.LlmApiKeySecretName,
            LlmApiKey = normalized.LlmApiKey,
            ApiKeyEnvironmentVariable = normalized.ApiKeyEnvironmentVariable,
            SubscriptionCredentialJson = normalized.SubscriptionCredentialJson,
            SubscriptionCredentialFileName = normalized.SubscriptionCredentialFileName,
            SubscriptionCredentialMountPath = normalized.SubscriptionCredentialMountPath,
            CodexAuthJson = normalized.CodexAuthJson,
            UpdatedAt = clock.UtcNow
        };

        await store.UpsertAsync(settings, cancellationToken);
        return ToResponse(settings);
    }

    public async Task<ResolvedAiSettings> ResolveAsync(CancellationToken cancellationToken)
    {
        var saved = await store.GetAsync(cancellationToken);
        var defaults = openHandsOptions.Value;
        var agentKind = NormalizeAgentKind(TrimToNull(saved?.AgentKind) ?? AgentKinds.OpenHands);
        var acpProvider = NormalizeAcpProvider(TrimToNull(saved?.AcpProvider) ?? AcpProviders.Custom);
        var authMethod = NormalizeAuthMethod(TrimToNull(saved?.AuthMethod) ?? TrimToNull(defaults.AuthMethod) ?? OpenHandsAuthMethods.ApiKey);
        var endpointUrl = TrimToNull(saved?.EndpointUrl) ?? TrimToNull(defaults.EndpointUrl);
        ValidateEndpointUrl(endpointUrl);

        var apiKeyEnvironmentVariable = TrimToNull(saved?.ApiKeyEnvironmentVariable) ?? DefaultApiKeyEnvironmentVariable(agentKind, acpProvider);
        var subscriptionFileName = TrimToNull(saved?.SubscriptionCredentialFileName) ?? DefaultSubscriptionFileName(agentKind, acpProvider);
        var subscriptionMountPath = TrimToNull(saved?.SubscriptionCredentialMountPath) ?? DefaultSubscriptionMountPath(agentKind, acpProvider);

        return new ResolvedAiSettings(
            TrimToNull(saved?.Provider) ?? TrimToNull(defaults.Provider),
            TrimToNull(saved?.Model) ?? TrimToNull(defaults.DefaultModel),
            endpointUrl,
            agentKind,
            agentKind == AgentKinds.Acp ? acpProvider : null,
            NormalizeAcpCommand(TrimToNull(saved?.AcpCommand), agentKind, acpProvider),
            authMethod,
            TrimToNull(saved?.LlmApiKeySecretName) ?? TrimToNull(defaults.LlmApiKeySecretName),
            TrimToNull(saved?.LlmApiKey),
            apiKeyEnvironmentVariable,
            TrimToNull(saved?.SubscriptionCredentialJson),
            subscriptionFileName,
            subscriptionMountPath,
            TrimToNull(saved?.CodexAuthJson));
    }

    private static AiSettingsResponse ToResponse(ResolvedAiSettings settings)
        => new(
            settings.Provider,
            settings.Model,
            settings.EndpointUrl,
            settings.AgentKind,
            settings.AcpProvider,
            settings.AcpCommand,
            settings.AuthMethod,
            settings.LlmApiKeySecretName,
            !string.IsNullOrWhiteSpace(settings.LlmApiKeySecretName),
            !string.IsNullOrWhiteSpace(settings.LlmApiKey),
            settings.ApiKeyEnvironmentVariable,
            !string.IsNullOrWhiteSpace(settings.SubscriptionCredentialJson) || !string.IsNullOrWhiteSpace(settings.CodexAuthJson),
            settings.SubscriptionCredentialFileName,
            settings.SubscriptionCredentialMountPath);

    private static AiSettingsResponse ToResponse(AiSettings settings)
    {
        var resolved = new ResolvedAiSettings(
            TrimToNull(settings.Provider),
            TrimToNull(settings.Model),
            TrimToNull(settings.EndpointUrl),
            NormalizeAgentKind(TrimToNull(settings.AgentKind) ?? AgentKinds.OpenHands),
            TrimToNull(settings.AcpProvider),
            TrimToNull(settings.AcpCommand),
            NormalizeAuthMethod(settings.AuthMethod),
            TrimToNull(settings.LlmApiKeySecretName),
            TrimToNull(settings.LlmApiKey),
            TrimToNull(settings.ApiKeyEnvironmentVariable),
            TrimToNull(settings.SubscriptionCredentialJson),
            TrimToNull(settings.SubscriptionCredentialFileName),
            TrimToNull(settings.SubscriptionCredentialMountPath),
            TrimToNull(settings.CodexAuthJson));
        return ToResponse(resolved);
    }

    private static NormalizedAiSettings Normalize(UpdateAiSettingsRequest request, AiSettings? existing)
    {
        var agentKind = NormalizeAgentKind(TrimToNull(request.AgentKind) ?? AgentKinds.OpenHands);
        var acpProvider = agentKind == AgentKinds.Acp
            ? NormalizeAcpProvider(TrimToNull(request.AcpProvider) ?? AcpProviders.Custom)
            : null;
        var authMethod = NormalizeAuthMethod(TrimToNull(request.AuthMethod) ?? OpenHandsAuthMethods.ApiKey);
        var endpointUrl = TrimToNull(request.EndpointUrl);
        ValidateEndpointUrl(endpointUrl);

        var subscriptionCredentialJson = PreserveExistingWhenBlank(request.SubscriptionCredentialJson, existing?.SubscriptionCredentialJson);
        var codexAuthJson = PreserveExistingWhenBlank(request.CodexAuthJson, existing?.CodexAuthJson);
        if (!string.IsNullOrWhiteSpace(subscriptionCredentialJson))
        {
            ValidateJson(subscriptionCredentialJson, "Subscription credential JSON must be valid JSON.");
        }

        if (!string.IsNullOrWhiteSpace(codexAuthJson))
        {
            ValidateJson(codexAuthJson, "CodexAuthJson must be valid JSON.");
        }

        return new NormalizedAiSettings(
            TrimToNull(request.Provider),
            TrimToNull(request.Model),
            endpointUrl,
            agentKind,
            acpProvider,
            NormalizeAcpCommand(TrimToNull(request.AcpCommand), agentKind, acpProvider),
            authMethod,
            TrimToNull(request.LlmApiKeySecretName),
            PreserveExistingWhenBlank(request.LlmApiKey, existing?.LlmApiKey),
            TrimToNull(request.ApiKeyEnvironmentVariable) ?? DefaultApiKeyEnvironmentVariable(agentKind, acpProvider),
            subscriptionCredentialJson,
            TrimToNull(request.SubscriptionCredentialFileName) ?? DefaultSubscriptionFileName(agentKind, acpProvider),
            TrimToNull(request.SubscriptionCredentialMountPath) ?? DefaultSubscriptionMountPath(agentKind, acpProvider),
            codexAuthJson);
    }

    private static string NormalizeAgentKind(string agentKind)
    {
        if (Is(agentKind, AgentKinds.OpenHands)) return AgentKinds.OpenHands;
        if (Is(agentKind, AgentKinds.Acp)) return AgentKinds.Acp;
        throw new ArgumentException($"Unsupported agent kind '{agentKind}'. Supported values are '{AgentKinds.OpenHands}' and '{AgentKinds.Acp}'.");
    }

    private static string NormalizeAcpProvider(string acpProvider)
    {
        if (Is(acpProvider, AcpProviders.ClaudeCode)) return AcpProviders.ClaudeCode;
        if (Is(acpProvider, AcpProviders.Codex)) return AcpProviders.Codex;
        if (Is(acpProvider, AcpProviders.GeminiCli)) return AcpProviders.GeminiCli;
        if (Is(acpProvider, AcpProviders.Custom)) return AcpProviders.Custom;
        throw new ArgumentException($"Unsupported ACP provider '{acpProvider}'. Supported values are '{AcpProviders.ClaudeCode}', '{AcpProviders.Codex}', '{AcpProviders.GeminiCli}' and '{AcpProviders.Custom}'.");
    }

    private static string NormalizeAuthMethod(string authMethod)
    {
        if (Is(authMethod, OpenHandsAuthMethods.ApiKey)) return OpenHandsAuthMethods.ApiKey;
        if (Is(authMethod, OpenHandsAuthMethods.OpenHandsCloud)) return OpenHandsAuthMethods.OpenHandsCloud;
        if (Is(authMethod, OpenHandsAuthMethods.CodexSubscription)) return OpenHandsAuthMethods.CodexSubscription;
        throw new ArgumentException($"Unsupported auth method '{authMethod}'. Supported values are '{OpenHandsAuthMethods.ApiKey}', '{OpenHandsAuthMethods.OpenHandsCloud}' and '{OpenHandsAuthMethods.CodexSubscription}'.");
    }

    private static string? NormalizeAcpCommand(string? command, string agentKind, string? acpProvider)
    {
        if (agentKind != AgentKinds.Acp)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(command)) return command;
        return acpProvider switch
        {
            AcpProviders.ClaudeCode => "claude-code-acp",
            AcpProviders.Codex => "codex-acp",
            AcpProviders.GeminiCli => "gemini-acp",
            _ => null
        };
    }

    private static string? DefaultApiKeyEnvironmentVariable(string agentKind, string? acpProvider)
        => agentKind == AgentKinds.Acp
            ? acpProvider switch
            {
                AcpProviders.ClaudeCode => "ANTHROPIC_API_KEY",
                AcpProviders.Codex => "OPENAI_API_KEY",
                AcpProviders.GeminiCli => "GEMINI_API_KEY",
                _ => null
            }
            : "LLM_API_KEY";

    private static string? DefaultSubscriptionFileName(string agentKind, string? acpProvider)
        => agentKind == AgentKinds.Acp
            ? acpProvider switch
            {
                AcpProviders.ClaudeCode => ".credentials.json",
                AcpProviders.Codex => "auth.json",
                AcpProviders.GeminiCli => "oauth_creds.json",
                _ => null
            }
            : "auth.json";

    private static string? DefaultSubscriptionMountPath(string agentKind, string? acpProvider)
        => agentKind == AgentKinds.Acp
            ? acpProvider switch
            {
                AcpProviders.ClaudeCode => "/root/.claude",
                AcpProviders.Codex => "/root/.codex",
                AcpProviders.GeminiCli => "/root/.gemini",
                _ => null
            }
            : "/root/.codex";

    private static void ValidateEndpointUrl(string? endpointUrl)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl) || Uri.TryCreate(endpointUrl, UriKind.Absolute, out _)) return;
        throw new ArgumentException("EndpointUrl must be an absolute URL.");
    }

    private static void ValidateJson(string json, string message)
    {
        try { using var _ = JsonDocument.Parse(json); }
        catch (JsonException exception) { throw new ArgumentException(message, exception); }
    }

    private static string? PreserveExistingWhenBlank(string? submitted, string? existing)
        => submitted is null ? TrimToNull(existing) : TrimToNull(submitted);

    private static bool Is(string? actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static string? TrimToNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private sealed record NormalizedAiSettings(
        string? Provider,
        string? Model,
        string? EndpointUrl,
        string AgentKind,
        string? AcpProvider,
        string? AcpCommand,
        string AuthMethod,
        string? LlmApiKeySecretName,
        string? LlmApiKey,
        string? ApiKeyEnvironmentVariable,
        string? SubscriptionCredentialJson,
        string? SubscriptionCredentialFileName,
        string? SubscriptionCredentialMountPath,
        string? CodexAuthJson);
}

public sealed record ResolvedAiSettings(
    string? Provider,
    string? Model,
    string? EndpointUrl,
    string AgentKind,
    string? AcpProvider,
    string? AcpCommand,
    string AuthMethod,
    string? LlmApiKeySecretName,
    string? LlmApiKey,
    string? ApiKeyEnvironmentVariable,
    string? SubscriptionCredentialJson,
    string? SubscriptionCredentialFileName,
    string? SubscriptionCredentialMountPath,
    string? CodexAuthJson);