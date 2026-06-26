using System.Security.Cryptography;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Application.Integrations;

public sealed class DevOpsIntegrationService(IDevOpsIntegrationStore store, IClock clock)
{
    private static readonly IntegrationCapability[] GitHubCapabilities =
    [
        IntegrationCapability.WorkItems,
        IntegrationCapability.SourceControl,
        IntegrationCapability.Webhooks,
        IntegrationCapability.IdentityProvider
    ];

    public async Task<IntegrationDetail> CreateGitHubIntegrationAsync(
        CreateGitHubIntegrationRequest request,
        Uri requestBaseUri,
        CancellationToken cancellationToken)
    {
        ValidateGitHubAppFields(request.ClientId, request.ClientSecretReference);

        var now = clock.UtcNow;
        var integration = new DevOpsIntegration
        {
            ProviderType = DevOpsProviderType.GitHub,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? "GitHub" : request.DisplayName.Trim(),
            GitHubAppClientId = request.ClientId.Trim(),
            GitHubAppClientSecretReference = request.ClientSecretReference.Trim(),
            WebhookSecret = string.IsNullOrWhiteSpace(request.WebhookSecret) ? GenerateWebhookSecret() : request.WebhookSecret.Trim(),
            WebhookUrl = BuildWebhookUrl(requestBaseUri),
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
        ValidateGitHubAppFields(request.ClientId, request.ClientSecretReference);

        var integration = await store.GetAsync(integrationId, cancellationToken);
        if (integration is null)
        {
            return null;
        }

        EnsureGitHub(integration);
        integration.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? integration.DisplayName : request.DisplayName.Trim();
        integration.GitHubAppClientId = request.ClientId.Trim();
        integration.GitHubAppClientSecretReference = request.ClientSecretReference.Trim();
        integration.WebhookUrl = BuildWebhookUrl(requestBaseUri);
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

        EnsureGitHub(integration);
        integration.WebhookSecret = GenerateWebhookSecret();
        integration.WebhookUrl = BuildWebhookUrl(requestBaseUri);
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

        EnsureGitHub(integration);
        var repository = ParseGitHubRepositoryUrl(request.RepositoryUrl);
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

        EnsureGitHub(integration);
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

        EnsureGitHub(integration);
        integration.RequiresRestart = false;
        integration.UpdatedAt = clock.UtcNow;
        await store.UpdateAsync(integration, cancellationToken);
        return ToDetail(integration, requestBaseUri, integration.Repositories.Select(ToRepository).ToArray());
    }

    public static string BuildWebhookUrl(Uri requestBaseUri)
        => new Uri(requestBaseUri, "/api/webhooks/github").ToString();

    public static string GenerateWebhookSecret()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static GitHubRepositoryReference ParseGitHubRepositoryUrl(string repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Expected a GitHub repository URL like https://github.com/{owner}/{repo}.", nameof(repositoryUrl));
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new ArgumentException("Expected a GitHub repository URL like https://github.com/{owner}/{repo}.", nameof(repositoryUrl));
        }

        var name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? parts[1][..^4]
            : parts[1];

        if (string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Expected a GitHub repository URL like https://github.com/{owner}/{repo}.", nameof(repositoryUrl));
        }

        return new GitHubRepositoryReference(parts[0], name, $"https://github.com/{parts[0]}/{name}");
    }

    private static void ValidateGitHubAppFields(string clientId, string? clientSecretReference)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("GitHub App client id is required.", nameof(clientId));
        }

        if (string.IsNullOrWhiteSpace(clientSecretReference))
        {
            throw new ArgumentException("GitHub App client secret reference is required.", nameof(clientSecretReference));
        }
    }

    private static void EnsureGitHub(DevOpsIntegration integration)
    {
        if (integration.ProviderType != DevOpsProviderType.GitHub)
        {
            throw new InvalidOperationException("Only GitHub integrations are supported.");
        }
    }

    private static IntegrationSummary ToSummary(DevOpsIntegration integration)
        => new(
            integration.Id,
            integration.ProviderType.ToString(),
            integration.DisplayName,
            integration.GitHubAppClientId,
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
            integration.WebhookUrl,
            integration.WebhookSecret,
            integration.IdentityProviderEnabled,
            integration.RequiresRestart,
            GitHubCapabilities.Select(capability => capability.ToString()).ToArray(),
            new GitHubSetupInstructions(
                new Uri(requestBaseUri, "/api/auth/github/callback").ToString(),
                integration.WebhookUrl,
                integration.WebhookSecret,
                ["Issues: read/write", "Pull requests: read/write", "Contents: read/write", "Metadata: read-only"],
                ["issues", "issue_comment", "pull_request", "pull_request_review", "pull_request_review_comment"]),
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
    string ClientSecretReference,
    string? WebhookSecret);

public sealed record UpdateGitHubAppRequest(
    string? DisplayName,
    string ClientId,
    string ClientSecretReference);

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
    string WebhookUrl,
    string WebhookSecret,
    bool IdentityProviderEnabled,
    bool RequiresRestart,
    IReadOnlyList<string> Capabilities,
    GitHubSetupInstructions SetupInstructions,
    IReadOnlyList<ConnectedRepositoryResponse> Repositories,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record GitHubSetupInstructions(
    string CallbackUrl,
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
