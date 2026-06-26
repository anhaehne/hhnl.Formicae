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
        var normalized = Normalize(request);
        var settings = new AiSettings
        {
            Id = AiSettings.DefaultId,
            Provider = normalized.Provider,
            Model = normalized.Model,
            EndpointUrl = normalized.EndpointUrl,
            AuthMethod = normalized.AuthMethod,
            LlmApiKeySecretName = normalized.LlmApiKeySecretName,
            UpdatedAt = clock.UtcNow
        };

        await store.UpsertAsync(settings, cancellationToken);
        return ToResponse(settings);
    }

    public async Task<ResolvedAiSettings> ResolveAsync(CancellationToken cancellationToken)
    {
        var saved = await store.GetAsync(cancellationToken);
        var defaults = openHandsOptions.Value;
        var authMethod = NormalizeAuthMethod(TrimToNull(saved?.AuthMethod) ?? TrimToNull(defaults.AuthMethod) ?? OpenHandsAuthMethods.ApiKey);

        var endpointUrl = TrimToNull(saved?.EndpointUrl) ?? TrimToNull(defaults.EndpointUrl);
        ValidateEndpointUrl(endpointUrl);

        var provider = TrimToNull(saved?.Provider) ?? TrimToNull(defaults.Provider);
        var model = TrimToNull(saved?.Model) ?? TrimToNull(defaults.DefaultModel);
        var secretName = TrimToNull(saved?.LlmApiKeySecretName) ?? TrimToNull(defaults.LlmApiKeySecretName);

        return new ResolvedAiSettings(provider, model, endpointUrl, authMethod, secretName);
    }

    private static AiSettingsResponse ToResponse(ResolvedAiSettings settings)
        => new(
            settings.Provider,
            settings.Model,
            settings.EndpointUrl,
            settings.AuthMethod,
            settings.LlmApiKeySecretName,
            !string.IsNullOrWhiteSpace(settings.LlmApiKeySecretName));

    private static AiSettingsResponse ToResponse(AiSettings settings)
    {
        var secretName = TrimToNull(settings.LlmApiKeySecretName);
        return new AiSettingsResponse(
            TrimToNull(settings.Provider),
            TrimToNull(settings.Model),
            TrimToNull(settings.EndpointUrl),
            settings.AuthMethod,
            secretName,
            !string.IsNullOrWhiteSpace(secretName));
    }

    private static NormalizedAiSettings Normalize(UpdateAiSettingsRequest request)
    {
        var authMethod = TrimToNull(request.AuthMethod) ?? OpenHandsAuthMethods.ApiKey;
        authMethod = NormalizeAuthMethod(authMethod);

        var endpointUrl = TrimToNull(request.EndpointUrl);
        ValidateEndpointUrl(endpointUrl);

        return new NormalizedAiSettings(
            TrimToNull(request.Provider),
            TrimToNull(request.Model),
            endpointUrl,
            authMethod,
            TrimToNull(request.LlmApiKeySecretName));
    }

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

        throw new ArgumentException($"Unsupported auth method '{authMethod}'. Supported values are '{OpenHandsAuthMethods.ApiKey}' and '{OpenHandsAuthMethods.CodexSubscription}'.");
    }

    private static void ValidateEndpointUrl(string? endpointUrl)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl) || Uri.TryCreate(endpointUrl, UriKind.Absolute, out _))
        {
            return;
        }

        throw new ArgumentException("EndpointUrl must be an absolute URL.");
    }

    private static bool IsAuthMethod(string? actual, string expected)
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
        string AuthMethod,
        string? LlmApiKeySecretName);
}

public sealed record ResolvedAiSettings(
    string? Provider,
    string? Model,
    string? EndpointUrl,
    string AuthMethod,
    string? LlmApiKeySecretName);
