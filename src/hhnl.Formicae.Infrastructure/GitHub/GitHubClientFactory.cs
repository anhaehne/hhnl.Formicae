using Octokit;

namespace hhnl.Formicae.Infrastructure.GitHub;

public sealed class GitHubClientFactory : IGitHubClientFactory
{
    public GitHubClient CreateClient(bool requireToken)
    {
        var client = new GitHubClient(new ProductHeaderValue("hhnl-formicae"));
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            if (requireToken)
            {
                throw new InvalidOperationException("GITHUB_TOKEN is required for GitHub source control operations.");
            }

            return client;
        }

        client.Credentials = new Credentials(token);
        return client;
    }
}
