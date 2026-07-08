using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Infrastructure.GitHub;
using hhnl.Formicae.Infrastructure.Gitea;
using Microsoft.Extensions.DependencyInjection;

namespace hhnl.Formicae.Infrastructure.DevOps;

public sealed class DevOpsPlatformFactory(
    IDevOpsIntegrationStore integrationStore,
    IServiceProvider serviceProvider) : IDevOpsPlatformFactory
{
    public async Task<DevOpsPlatformContext> CreateForRepositoryAsync(string repositoryUrl, CancellationToken cancellationToken)
    {
        var connectedRepository = await FindConnectedRepositoryAsync(repositoryUrl, cancellationToken)
            ?? throw new InvalidOperationException($"Repository '{repositoryUrl}' is not connected to a DevOps integration.");
        var integration = connectedRepository.DevOpsIntegration
            ?? await integrationStore.GetAsync(connectedRepository.DevOpsIntegrationId, cancellationToken)
            ?? throw new InvalidOperationException($"DevOps integration for repository '{connectedRepository.RepositoryUrl}' was not found.");
        var repository = DevOpsReferenceParser.ParseRepositoryUrl(integration.ProviderType, connectedRepository.RepositoryUrl, integration.ServerUrl);
        IDevOpsPlatform platform = integration.ProviderType switch
        {
            DevOpsProviderType.GitHub => serviceProvider.GetRequiredService<GitHubDevOpsPlatform>(),
            DevOpsProviderType.Gitea => ActivatorUtilities.CreateInstance<GiteaDevOpsPlatform>(serviceProvider, integration),
            _ => throw new InvalidOperationException($"Unsupported DevOps provider '{integration.ProviderType}'.")
        };

        return new DevOpsPlatformContext(integration, connectedRepository, repository, platform);
    }

    private async Task<ConnectedRepository?> FindConnectedRepositoryAsync(string repositoryUrl, CancellationToken cancellationToken)
    {
        var repositories = await integrationStore.ListAllRepositoriesAsync(cancellationToken);
        foreach (var repository in repositories)
        {
            var integration = repository.DevOpsIntegration
                ?? await integrationStore.GetAsync(repository.DevOpsIntegrationId, cancellationToken);
            if (integration is null)
            {
                continue;
            }

            if (DevOpsReferenceParser.TryParseRepositoryUrl(integration.ProviderType, repositoryUrl, integration.ServerUrl, out var parsed)
                && string.Equals(parsed.RepositoryUrl, repository.RepositoryUrl, StringComparison.OrdinalIgnoreCase))
            {
                repository.DevOpsIntegration = integration;
                return repository;
            }
        }

        return null;
    }
}
