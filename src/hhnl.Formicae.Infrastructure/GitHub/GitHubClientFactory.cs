using hhnl.Formicae.Application.Integrations;
using Octokit;

namespace hhnl.Formicae.Infrastructure.GitHub;

public sealed class GitHubClientFactory(IDevOpsIntegrationStore? integrationStore = null, IGitHubAppClient? gitHubAppClient = null) : IGitHubClientFactory
{
    public GitHubClient CreateClient(bool requireToken)
        => new(new ProductHeaderValue("hhnl-formicae"));

    public async Task<GitHubClient> CreateClientForRepositoryAsync(string repositoryUrl, CancellationToken cancellationToken)
    {
        if (integrationStore is null)
        {
            throw new InvalidOperationException("GitHub integration store is required for repository-scoped GitHub clients.");
        }

        if (gitHubAppClient is null)
        {
            throw new InvalidOperationException("GitHub App client is required for repository-scoped GitHub clients.");
        }

        var repository = DevOpsIntegrationService.ParseGitHubRepositoryUrl(repositoryUrl);
        var connectedRepository = await integrationStore.GetRepositoryByUrlAsync(repository.RepositoryUrl, cancellationToken)
            ?? throw new InvalidOperationException($"GitHub repository '{repository.RepositoryUrl}' is not connected to an integration.");
        var integration = connectedRepository.DevOpsIntegration
            ?? await integrationStore.GetAsync(connectedRepository.DevOpsIntegrationId, cancellationToken)
            ?? throw new InvalidOperationException($"GitHub integration for repository '{repository.RepositoryUrl}' was not found.");
        if (!connectedRepository.InstallationId.HasValue)
        {
            throw new InvalidOperationException($"GitHub repository '{repository.RepositoryUrl}' is not connected through a GitHub App installation. Install or grant the GitHub App access to the repository, refresh the repository list, remove the existing repository record, and add it again from the available installation repositories list.");
        }

        var installationToken = await gitHubAppClient.CreateInstallationTokenAsync(integration, connectedRepository.InstallationId.Value, cancellationToken);
        if (string.IsNullOrWhiteSpace(installationToken))
        {
            throw new InvalidOperationException($"GitHub App installation token for repository '{repository.RepositoryUrl}' could not be created.");
        }

        return new GitHubClient(new ProductHeaderValue("hhnl-formicae"))
        {
            Credentials = new Credentials(installationToken)
        };
    }
}