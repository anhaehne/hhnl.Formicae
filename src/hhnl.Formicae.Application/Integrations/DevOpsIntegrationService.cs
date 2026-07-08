using System.Security.Cryptography;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Application.Integrations;

public sealed class DevOpsIntegrationService(IDevOpsIntegrationStore store, IClock clock, IGitHubAppClient? githubAppClient = null)
{
    private static readonly IntegrationCapability[] GitHubCapabilities =
    [
        IntegrationCapability.WorkItems,
        IntegrationCapability.SourceControl,
        IntegrationCapability.Webhooks,
        IntegrationCapability.IdentityProvider
    ];

    private static readonly IntegrationCapability[] GiteaCapabilities =
    [
        IntegrationCapability.WorkItems,
        IntegrationCapability.SourceControl,
        IntegrationCapability.Webhooks
    ];

    public async Task<IntegrationDetail> CreateGitHubIntegrationAsync(
        CreateGitHubIntegrationRequest request,
        Uri requestBaseUri,
        CancellationToken cancellationToken)
    {
        ValidateGitHubAppFields(request.ClientId, request.PrivateKey);

        var now = clock.UtcNow;
        var integration = new DevOpsIntegration
        {
            ProviderType = DevOpsProviderType.GitHub,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? "GitHub" : request.DisplayName.Trim(),
            ServerUrl = "https://github.com",
            GitHubAppClientId = request.ClientId.Trim(),
            GitHubAppClientSecretReference = NormalizeOptional(request.ClientSecretReference),
            GitHubAppPrivateKey = NormalizePrivateKey(request.PrivateKey),
            WebhookSecret = string.IsNullOrWhiteSpace(request.WebhookSecret) ? GenerateWebhookSecret() : request.WebhookSecret.Trim(),
            WebhookUrl = BuildWebhookUrl(requestBaseUri, DevOpsProviderType.GitHub),
            CreatedAt = now,
            UpdatedAt = now
        };

        await RefreshGitHubAppMetadataAsync(integration, cancellationToken);

        return ToDetail(await store.CreateAsync(integration, cancellationToken), requestBaseUri, []);
    }

    public async Task<IntegrationDetail> CreateGiteaIntegrationAsync(
        CreateGiteaIntegrationRequest request,
        Uri requestBaseUri,
        CancellationToken cancellationToken)
    {
        var serverUrl = DevOpsReferenceParser.NormalizeServerUrl(DevOpsProviderType.Gitea, request.ServerUrl).ToString();
        if (string.IsNullOrWhiteSpace(request.AccessToken))
        {
            throw new ArgumentException("Gitea access token is required.", nameof(request.AccessToken));
        }

        var now = clock.UtcNow;
        var integration = new DevOpsIntegration
        {
            ProviderType = DevOpsProviderType.Gitea,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? "Gitea" : request.DisplayName.Trim(),
            ServerUrl = serverUrl,
            AccessToken = request.AccessToken.Trim(),
            WebhookSecret = string.IsNullOrWhiteSpace(request.WebhookSecret) ? GenerateWebhookSecret() : request.WebhookSecret.Trim(),
            WebhookUrl = BuildWebhookUrl(requestBaseUri, DevOpsProviderType.Gitea),
            CreatedAt = now,
            UpdatedAt = now
        };

        return ToDetail(await store.CreateAsync(integration, cancellationToken), requestBaseUri, []);
    }

    public async Task<IReadOnlyList<IntegrationSummary>> ListAsync(CancellationToken cancellationToken)
        => (await store.ListAsync(cancellationToken)).Select(ToSummary).ToArray();

    public async Task<DevOpsIntegration?> GetDefaultGitHubIntegrationAsync(CancellationToken cancellationToken)
        => (await store.ListAsync(cancellationToken))
            .Where(integration => integration.ProviderType == DevOpsProviderType.GitHub)
            .OrderByDescending(integration => integration.IdentityProviderEnabled)
            .ThenByDescending(integration => integration.UpdatedAt)
            .FirstOrDefault();

    public async Task<IntegrationDetail?> GetAsync(Guid integrationId, Uri requestBaseUri, CancellationToken cancellationToken)
    {
        var integration = await store.GetAsync(integrationId, cancellationToken);
        if (integration is null)
        {
            return null;
        }

        return ToDetail(integration, requestBaseUri, integration.Repositories.Select(ToRepository).ToArray());
    }

    public Task<DevOpsIntegration?> GetRawAsync(Guid integrationId, CancellationToken cancellationToken)
        => store.GetAsync(integrationId, cancellationToken);

    public Task<bool> DeleteAsync(Guid integrationId, CancellationToken cancellationToken)
        => store.DeleteAsync(integrationId, cancellationToken);

    public async Task<IntegrationDetail?> UpdateGitHubAppAsync(
        Guid integrationId,
        UpdateGitHubAppRequest request,
        Uri requestBaseUri,
        CancellationToken cancellationToken)
    {
        var integration = await store.GetAsync(integrationId, cancellationToken);
        if (integration is null)
        {
            return null;
        }

        EnsureProvider(integration, DevOpsProviderType.GitHub);
        var privateKey = NormalizeOptionalPrivateKey(request.PrivateKey);
        if (privateKey is null && string.IsNullOrWhiteSpace(integration.GitHubAppPrivateKey))
        {
            throw new ArgumentException("GitHub App private key is required.", nameof(request.PrivateKey));
        }

        ValidateGitHubAppFields(request.ClientId, privateKey ?? integration.GitHubAppPrivateKey);
        integration.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? integration.DisplayName : request.DisplayName.Trim();
        integration.GitHubAppClientId = request.ClientId.Trim();
        integration.GitHubAppClientSecretReference = NormalizeOptional(request.ClientSecretReference);
        if (privateKey is not null)
        {
            integration.GitHubAppPrivateKey = privateKey;
        }

        integration.ServerUrl = "https://github.com";
        integration.WebhookUrl = BuildWebhookUrl(requestBaseUri, DevOpsProviderType.GitHub);
        integration.UpdatedAt = clock.UtcNow;
        await RefreshGitHubAppMetadataAsync(integration, cancellationToken);

        await store.UpdateAsync(integration, cancellationToken);
        return ToDetail(integration, requestBaseUri, integration.Repositories.Select(ToRepository).ToArray());
    }

    public async Task<IntegrationDetail?> UpdateGiteaIntegrationAsync(
        Guid integrationId,
        UpdateGiteaIntegrationRequest request,
        Uri requestBaseUri,
        CancellationToken cancellationToken)
    {
        var integration = await store.GetAsync(integrationId, cancellationToken);
        if (integration is null)
        {
            return null;
        }

        EnsureProvider(integration, DevOpsProviderType.Gitea);
        integration.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? integration.DisplayName : request.DisplayName.Trim();
        integration.ServerUrl = DevOpsReferenceParser.NormalizeServerUrl(DevOpsProviderType.Gitea, request.ServerUrl).ToString();
        if (!string.IsNullOrWhiteSpace(request.AccessToken))
        {
            integration.AccessToken = request.AccessToken.Trim();
        }

        if (string.IsNullOrWhiteSpace(integration.AccessToken))
        {
            throw new ArgumentException("Gitea access token is required.", nameof(request.AccessToken));
        }

        if (!string.IsNullOrWhiteSpace(request.WebhookSecret))
        {
            integration.WebhookSecret = request.WebhookSecret.Trim();
        }

        integration.WebhookUrl = BuildWebhookUrl(requestBaseUri, DevOpsProviderType.Gitea);
        integration.UpdatedAt = clock.UtcNow;
        await store.UpdateAsync(integration, cancellationToken);
        return ToDetail(integration, requestBaseUri, integration.Repositories.Select(ToRepository).ToArray());
    }

    public async Task<IntegrationDetail?> RotateWebhookSecretAsync(Guid integrationId, Uri requestBaseUri, CancellationToken cancellationToken)
    {
        var integration = await store.GetAsync(integrationId, cancellationToken);
        if (integration is null)
        {
            return null;
        }

        EnsureProvider(integration, DevOpsProviderType.GitHub);
        integration.WebhookSecret = GenerateWebhookSecret();
        integration.WebhookUrl = BuildWebhookUrl(requestBaseUri, integration.ProviderType);
        integration.UpdatedAt = clock.UtcNow;
        await store.UpdateAsync(integration, cancellationToken);
        return ToDetail(integration, requestBaseUri, integration.Repositories.Select(ToRepository).ToArray());
    }

    public async Task<ConnectedRepositoryResponse?> AddRepositoryAsync(
        Guid integrationId,
        AddConnectedRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        var integration = await store.GetAsync(integrationId, cancellationToken);
        if (integration is null)
        {
            return null;
        }

        if (integration.ProviderType == DevOpsProviderType.GitHub && !request.InstallationId.HasValue)
        {
            throw new InvalidOperationException("Repository must be selected from a GitHub App installation. Install or grant the GitHub App access to this repository, refresh the repository list, and add the repository from the available installation repositories list.");
        }

        var repository = DevOpsReferenceParser.ParseRepositoryUrl(integration.ProviderType, request.RepositoryUrl, integration.ServerUrl);
        var existing = await store.GetRepositoryByUrlAsync(integrationId, repository.RepositoryUrl, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException("Repository is already connected to this integration.");
        }

        var now = clock.UtcNow;
        return ToRepository(await store.AddRepositoryAsync(new ConnectedRepository
        {
            DevOpsIntegrationId = integrationId,
            Owner = repository.Owner,
            Name = repository.Name,
            RepositoryUrl = repository.RepositoryUrl,
            DefaultBranch = string.IsNullOrWhiteSpace(request.DefaultBranch) ? "main" : request.DefaultBranch.Trim(),
            InstallationId = request.InstallationId,
            InstallationAccount = string.IsNullOrWhiteSpace(request.InstallationAccount) ? null : request.InstallationAccount.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        }, cancellationToken));
    }

    public async Task<IReadOnlyList<ConnectedRepositoryResponse>?> ListRepositoriesAsync(Guid integrationId, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(integrationId, cancellationToken) is null)
        {
            return null;
        }

        return (await store.ListRepositoriesAsync(integrationId, cancellationToken)).Select(ToRepository).ToArray();
    }

    public async Task<bool?> DeleteRepositoryAsync(Guid integrationId, Guid repositoryId, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(integrationId, cancellationToken) is null)
        {
            return null;
        }

        return await store.DeleteRepositoryAsync(integrationId, repositoryId, cancellationToken);
    }

    public async Task<IntegrationDetail?> SetIdentityProviderEnabledAsync(
        Guid integrationId,
        bool enabled,
        Uri requestBaseUri,
        CancellationToken cancellationToken)
    {
        var integration = await store.GetAsync(integrationId, cancellationToken);
        if (integration is null)
        {
            return null;
        }

        EnsureProvider(integration, DevOpsProviderType.GitHub);
        if (enabled && string.IsNullOrWhiteSpace(integration.GitHubAppClientSecretReference))
        {
            throw new InvalidOperationException("GitHub App client secret reference is required before enabling the identity provider.");
        }

        integration.IdentityProviderEnabled = enabled;
        integration.RequiresRestart = enabled;
        integration.UpdatedAt = clock.UtcNow;
        await store.UpdateAsync(integration, cancellationToken);
        return ToDetail(integration, requestBaseUri, integration.Repositories.Select(ToRepository).ToArray());
    }

    public async Task<IntegrationDetail?> MarkIdentityProviderRestartedAsync(
        Guid integrationId,
        Uri requestBaseUri,
        CancellationToken cancellationToken)
    {
        var integration = await store.GetAsync(integrationId, cancellationToken);
        if (integration is null)
        {
            return null;
        }

        EnsureProvider(integration, DevOpsProviderType.GitHub);
        integration.RequiresRestart = false;
        integration.UpdatedAt = clock.UtcNow;
        await store.UpdateAsync(integration, cancellationToken);
        return ToDetail(integration, requestBaseUri, integration.Repositories.Select(ToRepository).ToArray());
    }

    public static string BuildWebhookUrl(Uri requestBaseUri)
        => BuildWebhookUrl(requestBaseUri, DevOpsProviderType.GitHub);

    public static string BuildWebhookUrl(Uri requestBaseUri, DevOpsProviderType providerType)
        => new Uri(requestBaseUri, providerType == DevOpsProviderType.Gitea ? "/api/webhooks/gitea" : "/api/webhooks/github").ToString();

    public static string BuildInstallationCallbackUrl(Uri requestBaseUri)
        => new Uri(requestBaseUri, "/api/auth/github/installations/callback").ToString();

    public static string BuildInstallationUrl(DevOpsIntegration integration)
    {
        if (string.IsNullOrWhiteSpace(integration.GitHubAppSlug))
        {
            return string.Empty;
        }

        var builder = new UriBuilder($"https://github.com/apps/{Uri.EscapeDataString(integration.GitHubAppSlug)}/installations/new")
        {
            Query = $"state={Uri.EscapeDataString(integration.Id.ToString())}"
        };
        return builder.Uri.ToString();
    }

    public static string GenerateWebhookSecret()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static GitHubRepositoryReference ParseGitHubRepositoryUrl(string repositoryUrl)
    {
        var repository = DevOpsReferenceParser.ParseRepositoryUrl(DevOpsProviderType.GitHub, repositoryUrl);
        return new GitHubRepositoryReference(repository.Owner, repository.Name, repository.RepositoryUrl);
    }

    private async Task RefreshGitHubAppMetadataAsync(DevOpsIntegration integration, CancellationToken cancellationToken)
    {
        if (githubAppClient is null)
        {
            return;
        }

        var metadata = await githubAppClient.GetAppMetadataAsync(integration, cancellationToken);
        integration.GitHubAppSlug = metadata.Slug;
    }

    private static void ValidateGitHubAppFields(string clientId, string? privateKey)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("GitHub App client id is required.", nameof(clientId));
        }

        _ = NormalizePrivateKey(privateKey);
    }

    public static string NormalizePrivateKey(string? privateKey)
    {
        if (string.IsNullOrWhiteSpace(privateKey))
        {
            throw new ArgumentException("GitHub App private key is required.", nameof(privateKey));
        }

        var normalized = privateKey.Trim().Replace("\\n", "\n", StringComparison.Ordinal);
        if (!normalized.Contains("BEGIN", StringComparison.Ordinal) || !normalized.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new ArgumentException("GitHub App private key must be a PEM private key.", nameof(privateKey));
        }

        return normalized;
    }

    private static string? NormalizeOptionalPrivateKey(string? privateKey)
        => string.IsNullOrWhiteSpace(privateKey) ? null : NormalizePrivateKey(privateKey);

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void EnsureProvider(DevOpsIntegration integration, DevOpsProviderType providerType)
    {
        if (integration.ProviderType != providerType)
        {
            throw new InvalidOperationException($"This operation is only supported for {providerType} integrations.");
        }
    }

    private static IntegrationSummary ToSummary(DevOpsIntegration integration)
        => new(
            integration.Id,
            integration.ProviderType.ToString(),
            integration.DisplayName,
            integration.GitHubAppClientId,
            integration.GitHubAppSlug,
            integration.ServerUrl,
            integration.WebhookUrl,
            integration.IdentityProviderEnabled,
            integration.RequiresRestart,
            integration.CreatedAt,
            integration.UpdatedAt);

    private static IntegrationDetail ToDetail(
        DevOpsIntegration integration,
        Uri requestBaseUri,
        IReadOnlyList<ConnectedRepositoryResponse> repositories)
        => new(
            integration.Id,
            integration.ProviderType.ToString(),
            integration.DisplayName,
            integration.GitHubAppClientId,
            integration.GitHubAppSlug,
            integration.ServerUrl,
            integration.WebhookUrl,
            integration.WebhookSecret,
            integration.IdentityProviderEnabled,
            integration.RequiresRestart,
            (integration.ProviderType == DevOpsProviderType.Gitea ? GiteaCapabilities : GitHubCapabilities).Select(capability => capability.ToString()).ToArray(),
            new DevOpsSetupInstructions(
                new Uri(requestBaseUri, "/api/auth/github/callback").ToString(),
                BuildInstallationCallbackUrl(requestBaseUri),
                integration.ProviderType == DevOpsProviderType.GitHub ? BuildInstallationUrl(integration) : string.Empty,
                integration.WebhookUrl,
                integration.WebhookSecret,
                integration.ProviderType == DevOpsProviderType.Gitea
                    ? ["Repository: read/write", "Issues: read/write", "Pull requests: read/write", "Contents: read/write"]
                    : ["Issues: read/write", "Pull requests: read/write", "Contents: read/write", "Metadata: read-only"],
                integration.ProviderType == DevOpsProviderType.Gitea
                    ? ["issues", "issue_comment", "pull_request", "pull_request_review", "pull_request_review_comment"]
                    : ["issues", "issue_comment", "pull_request", "pull_request_review", "pull_request_review_comment"]),
            repositories,
            integration.CreatedAt,
            integration.UpdatedAt);

    private static ConnectedRepositoryResponse ToRepository(ConnectedRepository repository)
        => new(
            repository.Id,
            repository.Owner,
            repository.Name,
            repository.RepositoryUrl,
            repository.DefaultBranch,
            repository.InstallationId,
            repository.InstallationAccount,
            repository.CreatedAt,
            repository.UpdatedAt);
}

public sealed record CreateGitHubIntegrationRequest(
    string DisplayName,
    string ClientId,
    string? ClientSecretReference,
    string PrivateKey,
    string? WebhookSecret);

public sealed record UpdateGitHubAppRequest(
    string? DisplayName,
    string ClientId,
    string? ClientSecretReference,
    string? PrivateKey);

public sealed record CreateGiteaIntegrationRequest(
    string DisplayName,
    string ServerUrl,
    string AccessToken,
    string? WebhookSecret);

public sealed record UpdateGiteaIntegrationRequest(
    string? DisplayName,
    string ServerUrl,
    string? AccessToken,
    string? WebhookSecret);

public sealed record AddConnectedRepositoryRequest(
    string RepositoryUrl,
    string? DefaultBranch,
    long? InstallationId,
    string? InstallationAccount);

public sealed record UpdateIdentityProviderRequest(bool Enabled);

public sealed record IntegrationSummary(
    Guid Id,
    string ProviderType,
    string DisplayName,
    string GitHubAppClientId,
    string? GitHubAppSlug,
    string? ServerUrl,
    string WebhookUrl,
    bool IdentityProviderEnabled,
    bool RequiresRestart,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record IntegrationDetail(
    Guid Id,
    string ProviderType,
    string DisplayName,
    string GitHubAppClientId,
    string? GitHubAppSlug,
    string? ServerUrl,
    string WebhookUrl,
    string WebhookSecret,
    bool IdentityProviderEnabled,
    bool RequiresRestart,
    IReadOnlyList<string> Capabilities,
    DevOpsSetupInstructions SetupInstructions,
    IReadOnlyList<ConnectedRepositoryResponse> Repositories,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DevOpsSetupInstructions(
    string CallbackUrl,
    string InstallationCallbackUrl,
    string InstallationUrl,
    string WebhookUrl,
    string WebhookSecret,
    IReadOnlyList<string> RequiredRepositoryPermissions,
    IReadOnlyList<string> RequiredWebhookEvents);

public sealed record ConnectedRepositoryResponse(
    Guid Id,
    string Owner,
    string Name,
    string RepositoryUrl,
    string DefaultBranch,
    long? InstallationId,
    string? InstallationAccount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record GitHubRepositoryReference(string Owner, string Name, string RepositoryUrl);
