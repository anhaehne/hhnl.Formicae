using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Infrastructure.GitHub;
using Octokit;

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

    [Fact]
    public async Task CreateClientForRepositoryAsync_uses_connected_integration_token()
    {
        var store = new InMemoryDevOpsIntegrationStore();
        var integration = await store.CreateAsync(new DevOpsIntegration
        {
            ProviderType = DevOpsProviderType.GitHub,
            DisplayName = "GitHub",
            GitHubAppClientId = "client-id",
            GitHubOAuthAccessToken = "integration-token",
            WebhookSecret = "webhook-secret",
            WebhookUrl = "https://formicae.example/api/webhooks/github",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        await store.AddRepositoryAsync(new ConnectedRepository
        {
            DevOpsIntegrationId = integration.Id,
            Owner = "acme",
            Name = "widgets",
            RepositoryUrl = "https://github.com/acme/widgets",
            DefaultBranch = "main"
        }, CancellationToken.None);

        var client = await new GitHubClientFactory(store).CreateClientForRepositoryAsync("https://github.com/acme/widgets", CancellationToken.None);

        Assert.NotEqual(Credentials.Anonymous, client.Credentials);
    }

    [Fact]
    public async Task CreateClientForRepositoryAsync_rejects_unauthenticated_integration()
    {
        var store = new InMemoryDevOpsIntegrationStore();
        var integration = await store.CreateAsync(new DevOpsIntegration
        {
            ProviderType = DevOpsProviderType.GitHub,
            DisplayName = "GitHub",
            GitHubAppClientId = "client-id",
            WebhookSecret = "webhook-secret",
            WebhookUrl = "https://formicae.example/api/webhooks/github",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        await store.AddRepositoryAsync(new ConnectedRepository
        {
            DevOpsIntegrationId = integration.Id,
            Owner = "acme",
            Name = "widgets",
            RepositoryUrl = "https://github.com/acme/widgets",
            DefaultBranch = "main"
        }, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GitHubClientFactory(store).CreateClientForRepositoryAsync("https://github.com/acme/widgets", CancellationToken.None));

        Assert.Contains("is not authenticated", exception.Message);
    }
}