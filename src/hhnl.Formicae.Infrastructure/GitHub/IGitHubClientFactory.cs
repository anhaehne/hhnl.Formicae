using Octokit;

namespace hhnl.Formicae.Infrastructure.GitHub;

public interface IGitHubClientFactory
{
    GitHubClient CreateClient(bool requireToken);
}
