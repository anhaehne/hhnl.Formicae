using hhnl.Formicae.Application.Integrations;

namespace hhnl.Formicae.Infrastructure.Fakes;

public sealed class InMemoryDevOpsIntegrationStore : IDevOpsIntegrationStore
{
    private readonly object gate = new();
    private readonly List<DevOpsIntegration> integrations = [];
    private readonly List<ConnectedRepository> repositories = [];

    public Task<DevOpsIntegration> CreateAsync(DevOpsIntegration integration, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            integrations.Add(Clone(integration));
            return Task.FromResult(Clone(integration));
        }
    }

    public Task<IReadOnlyList<DevOpsIntegration>> ListAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<DevOpsIntegration>>(integrations.Select(Clone).ToArray());
        }
    }

    public Task<DevOpsIntegration?> GetAsync(Guid integrationId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var integration = integrations.FirstOrDefault(item => item.Id == integrationId);
            if (integration is null)
            {
                return Task.FromResult<DevOpsIntegration?>(null);
            }

            var clone = Clone(integration);
            clone.Repositories = repositories.Where(repository => repository.DevOpsIntegrationId == integrationId).Select(Clone).ToList();
            return Task.FromResult<DevOpsIntegration?>(clone);
        }
    }

    public Task<DevOpsIntegration?> GetGitHubIdentityProviderAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(integrations
                .Where(integration => integration.ProviderType == DevOpsProviderType.GitHub && integration.IdentityProviderEnabled)
                .Select(Clone)
                .FirstOrDefault());
        }
    }

    public Task<bool> AnyIdentityProviderEnabledAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(integrations.Any(integration => integration.IdentityProviderEnabled));
        }
    }

    public Task UpdateAsync(DevOpsIntegration integration, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var index = integrations.FindIndex(item => item.Id == integration.Id);
            if (index >= 0)
            {
                integrations[index] = Clone(integration);
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid integrationId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var removed = integrations.RemoveAll(integration => integration.Id == integrationId) > 0;
            if (removed)
            {
                repositories.RemoveAll(repository => repository.DevOpsIntegrationId == integrationId);
            }

            return Task.FromResult(removed);
        }
    }

    public Task<ConnectedRepository> AddRepositoryAsync(ConnectedRepository repository, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            repositories.Add(Clone(repository));
            return Task.FromResult(Clone(repository));
        }
    }

    public Task<IReadOnlyList<ConnectedRepository>> ListRepositoriesAsync(Guid integrationId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<ConnectedRepository>>(repositories
                .Where(repository => repository.DevOpsIntegrationId == integrationId)
                .Select(Clone)
                .ToArray());
        }
    }

    public Task<IReadOnlyList<ConnectedRepository>> ListAllRepositoriesAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<ConnectedRepository>>(repositories
                .Select(Clone)
                .ToArray());
        }
    }

    public Task<bool> DeleteRepositoryAsync(Guid integrationId, Guid repositoryId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(repositories.RemoveAll(repository =>
                repository.DevOpsIntegrationId == integrationId && repository.Id == repositoryId) > 0);
        }
    }

    public Task<ConnectedRepository?> GetRepositoryByUrlAsync(Guid integrationId, string repositoryUrl, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(repositories
                .Where(repository => repository.DevOpsIntegrationId == integrationId)
                .Where(repository => string.Equals(repository.RepositoryUrl, repositoryUrl, StringComparison.OrdinalIgnoreCase))
                .Select(Clone)
                .FirstOrDefault());
        }
    }


    public Task<ConnectedRepository?> GetRepositoryByUrlAsync(string repositoryUrl, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var repository = repositories
                .Where(repository => string.Equals(repository.RepositoryUrl, repositoryUrl, StringComparison.OrdinalIgnoreCase))
                .Select(Clone)
                .FirstOrDefault();
            if (repository is not null)
            {
                repository.DevOpsIntegration = integrations
                    .Where(integration => integration.Id == repository.DevOpsIntegrationId)
                    .Select(Clone)
                    .FirstOrDefault();
            }

            return Task.FromResult(repository);
        }
    }
    private static DevOpsIntegration Clone(DevOpsIntegration integration)
        => new()
        {
            Id = integration.Id,
            ProviderType = integration.ProviderType,
            DisplayName = integration.DisplayName,
            ServerUrl = integration.ServerUrl,
            AccessToken = integration.AccessToken,
            GitHubAppClientId = integration.GitHubAppClientId,
            GitHubAppSlug = integration.GitHubAppSlug,
            GitHubAppClientSecretReference = integration.GitHubAppClientSecretReference,
            GitHubAppPrivateKey = integration.GitHubAppPrivateKey,
            GitHubOAuthAccessToken = integration.GitHubOAuthAccessToken,
            WebhookSecret = integration.WebhookSecret,
            WebhookUrl = integration.WebhookUrl,
            IdentityProviderEnabled = integration.IdentityProviderEnabled,
            RequiresRestart = integration.RequiresRestart,
            CreatedAt = integration.CreatedAt,
            UpdatedAt = integration.UpdatedAt,
            Repositories = integration.Repositories.Select(Clone).ToList()
        };

    private static ConnectedRepository Clone(ConnectedRepository repository)
        => new()
        {
            Id = repository.Id,
            DevOpsIntegrationId = repository.DevOpsIntegrationId,
            Owner = repository.Owner,
            Name = repository.Name,
            RepositoryUrl = repository.RepositoryUrl,
            DefaultBranch = repository.DefaultBranch,
            InstallationId = repository.InstallationId,
            InstallationAccount = repository.InstallationAccount,
            CreatedAt = repository.CreatedAt,
            UpdatedAt = repository.UpdatedAt
        };
}
