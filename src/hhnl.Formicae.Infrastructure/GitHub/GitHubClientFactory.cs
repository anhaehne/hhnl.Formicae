using hhnl.Formicae.Application.Integrations;
using Octokit;

namespace hhnl.Formicae.Infrastructure.GitHub;

public sealed class GitHubClientFactory(IDevOpsIntegrationStore? integrationStore = null) : IGitHubClientFactory
{
    public GitHubClient CreateClient(bool requireToken)
        => new(new ProductHeaderValue("hhnl-formicae"));

    public async Task<GitHubClient> CreateClientForRepositoryAsync(string repositoryUrl, CancellationToken cancellationToken)
    {
        if (integrationStore is null)
        {
            throw new InvalidOperationException("GitHub integration store is required for repository-scoped GitHub clients.");
        }

        var repository = DevOpsIntegrationService.ParseGitHubRepositoryUrl(repositoryUrl);
        var connectedRepository = await integrationStore.GetRepositoryByUrlAsync(repository.RepositoryUrl, cancellationToken)
            ?? throw new InvalidOperationException($"GitHub repository '{repository.RepositoryUrl}' is not connected to an integration.");
        var integration = connectedRepository.DevOpsIntegration
            ?? await integrationStore.GetAsync(connectedRepository.DevOpsIntegrationId, cancellationToken)
            ?? throw new InvalidOperationException($"GitHub integration for repository '{repository.RepositoryUrl}' was not found.");
        if (string.IsNullOrWhiteSpace(integration.GitHubOAuthAccessToken))
        {
            throw new InvalidOperationException($"GitHub integration '{integration.DisplayName}' is not authenticated. Authenticate GitHub for the integration before running workflows.");
        }

        return new GitHubClient(new ProductHeaderValue("hhnl-formicae"))
        {
            Credentials = new Credentials(integration.GitHubOAuthAccessToken)
        };
    }
}