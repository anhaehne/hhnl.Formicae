using hhnl.Formicae.Application.Integrations;
using Microsoft.EntityFrameworkCore;

namespace hhnl.Formicae.Infrastructure.Persistence;

public sealed class EfDevOpsIntegrationStore(FormicaeDbContext dbContext) : IDevOpsIntegrationStore
{
    public async Task<DevOpsIntegration> CreateAsync(DevOpsIntegration integration, CancellationToken cancellationToken)
    {
        dbContext.DevOpsIntegrations.Add(integration);
        await dbContext.SaveChangesAsync(cancellationToken);
        return integration;
    }

    public async Task<IReadOnlyList<DevOpsIntegration>> ListAsync(CancellationToken cancellationToken)
        => await dbContext.DevOpsIntegrations
            .AsNoTracking()
            .OrderByDescending(integration => integration.UpdatedAt)
            .ToArrayAsync(cancellationToken);

    public async Task<DevOpsIntegration?> GetAsync(Guid integrationId, CancellationToken cancellationToken)
        => await dbContext.DevOpsIntegrations
            .Include(integration => integration.Repositories)
            .FirstOrDefaultAsync(integration => integration.Id == integrationId, cancellationToken);

    public async Task<DevOpsIntegration?> GetGitHubIdentityProviderAsync(CancellationToken cancellationToken)
        => await dbContext.DevOpsIntegrations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                integration => integration.ProviderType == DevOpsProviderType.GitHub && integration.IdentityProviderEnabled,
                cancellationToken);

    public async Task<bool> AnyIdentityProviderEnabledAsync(CancellationToken cancellationToken)
        => await dbContext.DevOpsIntegrations
            .AsNoTracking()
            .AnyAsync(integration => integration.IdentityProviderEnabled, cancellationToken);

    public async Task UpdateAsync(DevOpsIntegration integration, CancellationToken cancellationToken)
    {
        dbContext.DevOpsIntegrations.Update(integration);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ConnectedRepository> AddRepositoryAsync(ConnectedRepository repository, CancellationToken cancellationToken)
    {
        dbContext.ConnectedRepositories.Add(repository);
        await dbContext.SaveChangesAsync(cancellationToken);
        return repository;
    }

    public async Task<IReadOnlyList<ConnectedRepository>> ListRepositoriesAsync(Guid integrationId, CancellationToken cancellationToken)
        => await dbContext.ConnectedRepositories
            .AsNoTracking()
            .Where(repository => repository.DevOpsIntegrationId == integrationId)
            .OrderBy(repository => repository.Owner)
            .ThenBy(repository => repository.Name)
            .ToArrayAsync(cancellationToken);

    public async Task<ConnectedRepository?> GetRepositoryByUrlAsync(Guid integrationId, string repositoryUrl, CancellationToken cancellationToken)
        => await dbContext.ConnectedRepositories
            .AsNoTracking()
            .FirstOrDefaultAsync(
                repository => repository.DevOpsIntegrationId == integrationId
                    && repository.RepositoryUrl.ToLower() == repositoryUrl.ToLower(),
                cancellationToken);
}
