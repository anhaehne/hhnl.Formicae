namespace hhnl.Formicae.Application.Integrations;

public interface IDevOpsIntegrationStore
{
    Task<DevOpsIntegration> CreateAsync(DevOpsIntegration integration, CancellationToken cancellationToken);

    Task<IReadOnlyList<DevOpsIntegration>> ListAsync(CancellationToken cancellationToken);

    Task<DevOpsIntegration?> GetAsync(Guid integrationId, CancellationToken cancellationToken);

    Task<DevOpsIntegration?> GetGitHubIdentityProviderAsync(CancellationToken cancellationToken);

    Task<bool> AnyIdentityProviderEnabledAsync(CancellationToken cancellationToken);

    Task UpdateAsync(DevOpsIntegration integration, CancellationToken cancellationToken);

    Task<ConnectedRepository> AddRepositoryAsync(ConnectedRepository repository, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConnectedRepository>> ListRepositoriesAsync(Guid integrationId, CancellationToken cancellationToken);

    Task<ConnectedRepository?> GetRepositoryByUrlAsync(Guid integrationId, string repositoryUrl, CancellationToken cancellationToken);
}
