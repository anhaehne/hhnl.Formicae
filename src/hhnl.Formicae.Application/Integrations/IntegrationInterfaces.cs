namespace hhnl.Formicae.Application.Integrations;

public interface IDevOpsIntegrationStore
{
    Task<DevOpsIntegration> CreateAsync(DevOpsIntegration integration, CancellationToken cancellationToken);

    Task<IReadOnlyList<DevOpsIntegration>> ListAsync(CancellationToken cancellationToken);

    Task<DevOpsIntegration?> GetAsync(Guid integrationId, CancellationToken cancellationToken);

    Task<DevOpsIntegration?> GetGitHubIdentityProviderAsync(CancellationToken cancellationToken);

    Task<bool> AnyIdentityProviderEnabledAsync(CancellationToken cancellationToken);

    Task UpdateAsync(DevOpsIntegration integration, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid integrationId, CancellationToken cancellationToken);

    Task<ConnectedRepository> AddRepositoryAsync(ConnectedRepository repository, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConnectedRepository>> ListRepositoriesAsync(Guid integrationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConnectedRepository>> ListAllRepositoriesAsync(CancellationToken cancellationToken);

    Task<bool> DeleteRepositoryAsync(Guid integrationId, Guid repositoryId, CancellationToken cancellationToken);

    Task<ConnectedRepository?> GetRepositoryByUrlAsync(Guid integrationId, string repositoryUrl, CancellationToken cancellationToken);

    Task<ConnectedRepository?> GetRepositoryByUrlAsync(string repositoryUrl, CancellationToken cancellationToken);
}
public interface IGitHubAppClient
{
    Task<GitHubAppMetadata> GetAppMetadataAsync(DevOpsIntegration integration, CancellationToken cancellationToken);

    Task<IReadOnlyList<GitHubInstallationRepository>> ListInstallationRepositoriesAsync(DevOpsIntegration integration, CancellationToken cancellationToken);

    Task<string> CreateInstallationTokenAsync(DevOpsIntegration integration, long installationId, CancellationToken cancellationToken);
}

public sealed record GitHubAppMetadata(string Slug, string HtmlUrl);

public sealed record GitHubInstallationRepository(
    string Owner,
    string Name,
    string RepositoryUrl,
    string DefaultBranch,
    bool Private,
    long InstallationId,
    string? InstallationAccount);
