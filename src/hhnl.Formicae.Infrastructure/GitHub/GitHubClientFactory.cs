using Octokit;

namespace hhnl.Formicae.Infrastructure.GitHub;

public sealed class GitHubClientFactory : IGitHubClientFactory
{
    public GitHubClient CreateClient(bool requireToken)
        => new(new ProductHeaderValue("hhnl-formicae"));
}
