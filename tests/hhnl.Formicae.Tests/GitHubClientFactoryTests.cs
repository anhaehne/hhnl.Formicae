using hhnl.Formicae.Infrastructure.GitHub;

namespace hhnl.Formicae.Tests;

public sealed class GitHubClientFactoryTests
{
    [Fact]
    public void CreateClient_does_not_require_environment_token()
    {
        var previous = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
        try
        {
            var client = new GitHubClientFactory().CreateClient(requireToken: true);

            Assert.NotNull(client);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", previous);
        }
    }
}
