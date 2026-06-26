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
    public async Task CreateClientForRepositoryAsync_uses_installation_token()
    {
        var store = new InMemoryDevOpsIntegrationStore();
        var integration = await store.CreateAsync(new DevOpsIntegration
        {
            ProviderType = DevOpsProviderType.GitHub,
            DisplayName = "GitHub",
            GitHubAppClientId = "client-id",
            GitHubAppPrivateKey = "private-key",
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
            DefaultBranch = "main",
            InstallationId = 123
        }, CancellationToken.None);
        var appClient = new RecordingGitHubAppClient("installation-token");

        var client = await new GitHubClientFactory(store, appClient).CreateClientForRepositoryAsync("https://github.com/acme/widgets", CancellationToken.None);

        Assert.NotEqual(Credentials.Anonymous, client.Credentials);
        Assert.Equal(123, appClient.InstallationId);
        Assert.Equal(integration.Id, appClient.IntegrationId);
    }

    [Fact]
    public async Task CreateClientForRepositoryAsync_rejects_repository_without_installation()
    {
        var store = new InMemoryDevOpsIntegrationStore();
        var integration = await store.CreateAsync(new DevOpsIntegration
        {
            ProviderType = DevOpsProviderType.GitHub,
            DisplayName = "GitHub",
            GitHubAppClientId = "client-id",
            GitHubAppPrivateKey = "private-key",
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
            new GitHubClientFactory(store, new RecordingGitHubAppClient("installation-token")).CreateClientForRepositoryAsync("https://github.com/acme/widgets", CancellationToken.None));

        Assert.Contains("GitHub App installation", exception.Message);
    }

    [Fact]
    public async Task CreateClientForRepositoryAsync_requires_github_app_client()
    {
        var store = new InMemoryDevOpsIntegrationStore();
        var integration = await store.CreateAsync(new DevOpsIntegration
        {
            ProviderType = DevOpsProviderType.GitHub,
            DisplayName = "GitHub",
            GitHubAppClientId = "client-id",
            GitHubAppPrivateKey = "private-key",
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
            DefaultBranch = "main",
            InstallationId = 123
        }, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GitHubClientFactory(store).CreateClientForRepositoryAsync("https://github.com/acme/widgets", CancellationToken.None));

        Assert.Contains("GitHub App client", exception.Message);
    }

    private sealed class RecordingGitHubAppClient(string token) : IGitHubAppClient
    {
        public Guid? IntegrationId { get; private set; }
        public long? InstallationId { get; private set; }

        public Task<GitHubAppMetadata> GetAppMetadataAsync(DevOpsIntegration integration, CancellationToken cancellationToken)
            => Task.FromResult(new GitHubAppMetadata("formicae-test", "https://github.com/apps/formicae-test"));

        public Task<IReadOnlyList<GitHubInstallationRepository>> ListInstallationRepositoriesAsync(DevOpsIntegration integration, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<GitHubInstallationRepository>>([]);

        public Task<string> CreateInstallationTokenAsync(DevOpsIntegration integration, long installationId, CancellationToken cancellationToken)
        {
            IntegrationId = integration.Id;
            InstallationId = installationId;
            return Task.FromResult(token);
        }
    }
}