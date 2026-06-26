using hhnl.Formicae.Infrastructure.GitHub;

namespace hhnl.Formicae.Tests;

public sealed class GitHubClientFactoryTests
{
    [Fact]
    public void CreateClient_without_token_allows_read_only_client_when_token_is_optional()
    {
        var previous = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
        try
        {
            var client = new GitHubClientFactory().CreateClient(requireToken: false);

            Assert.NotNull(client);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", previous);
        }
    }

    [Fact]
    public void CreateClient_without_token_throws_when_token_is_required()
    {
        var previous = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
        try
        {
            Assert.Throws<InvalidOperationException>(() => new GitHubClientFactory().CreateClient(requireToken: true));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", previous);
        }
    }
}
