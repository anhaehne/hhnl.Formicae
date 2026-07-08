using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class DevOpsIntegrationServiceTests
{
    private static readonly Uri RequestBaseUri = new("https://formicae.example");
    private const string ValidPrivateKey = "-----BEGIN RSA PRIVATE KEY-----\\nkey\\n-----END RSA PRIVATE KEY-----";

    [Fact]
    public async Task CreateGitHubIntegrationAsync_generates_secret_and_setup_urls()
    {
        var service = CreateService();

        var integration = await service.CreateGitHubIntegrationAsync(
            new CreateGitHubIntegrationRequest("GitHub Prod", "client-id", "client-secret-ref", ValidPrivateKey, null),
            RequestBaseUri,
            CancellationToken.None);

        Assert.Equal("GitHub Prod", integration.DisplayName);
        Assert.Equal("client-id", integration.GitHubAppClientId);
        Assert.Equal("https://formicae.example/api/webhooks/github", integration.WebhookUrl);
        Assert.Equal("https://formicae.example/api/auth/github/callback", integration.SetupInstructions.CallbackUrl);
        Assert.Equal("https://formicae.example/api/auth/github/installations/callback", integration.SetupInstructions.InstallationCallbackUrl);
        Assert.Equal(64, integration.WebhookSecret.Length);
        Assert.Contains("issues", integration.SetupInstructions.RequiredWebhookEvents);
    }

    [Theory]
    [InlineData("", ValidPrivateKey)]
    [InlineData("client-id", "")]
    public async Task CreateGitHubIntegrationAsync_validates_required_fields(string clientId, string privateKey)
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateGitHubIntegrationAsync(
            new CreateGitHubIntegrationRequest("GitHub", clientId, "client-secret-ref", privateKey, null),
            RequestBaseUri,
            CancellationToken.None));
    }

    [Theory]
    [InlineData("https://github.com/acme/widgets", "acme", "widgets")]
    [InlineData("https://github.com/acme/widgets.git", "acme", "widgets")]
    public void ParseGitHubRepositoryUrl_accepts_supported_urls(string url, string owner, string name)
    {
        var repository = DevOpsIntegrationService.ParseGitHubRepositoryUrl(url);

        Assert.Equal(owner, repository.Owner);
        Assert.Equal(name, repository.Name);
        Assert.Equal($"https://github.com/{owner}/{name}", repository.RepositoryUrl);
    }

    [Theory]
    [InlineData("http://github.com/acme/widgets")]
    [InlineData("https://example.com/acme/widgets")]
    [InlineData("https://github.com/acme/widgets/issues/1")]
    public void ParseGitHubRepositoryUrl_rejects_invalid_urls(string url)
    {
        Assert.Throws<ArgumentException>(() => DevOpsIntegrationService.ParseGitHubRepositoryUrl(url));
    }

    [Theory]
    [InlineData("https://gitea.example/acme/widgets", "acme", "widgets")]
    [InlineData("https://gitea.example/acme/widgets.git", "acme", "widgets")]
    public void ParseRepositoryUrl_accepts_gitea_urls(string url, string owner, string name)
    {
        var repository = DevOpsReferenceParser.ParseRepositoryUrl(DevOpsProviderType.Gitea, url, "https://gitea.example");

        Assert.Equal(owner, repository.Owner);
        Assert.Equal(name, repository.Name);
        Assert.Equal($"https://gitea.example/{owner}/{name}", repository.RepositoryUrl);
    }

    [Theory]
    [InlineData("https://github.com/acme/widgets/issues/1", DevOpsProviderType.GitHub, null)]
    [InlineData("https://gitea.example/acme/widgets/issues/1", DevOpsProviderType.Gitea, "https://gitea.example")]
    public void ParseIssueUrl_accepts_provider_issue_urls(string url, DevOpsProviderType providerType, string? serverUrl)
    {
        var issue = DevOpsReferenceParser.ParseIssueUrl(providerType, url, serverUrl);

        Assert.Equal("acme", issue.Owner);
        Assert.Equal("widgets", issue.Repository);
        Assert.Equal(1, issue.Number);
    }

    [Theory]
    [InlineData("https://github.com/acme/widgets/pull/1", DevOpsProviderType.GitHub, null)]
    [InlineData("https://gitea.example/acme/widgets/pulls/1", DevOpsProviderType.Gitea, "https://gitea.example")]
    public void ParsePullRequestUrl_accepts_provider_pull_request_urls(string url, DevOpsProviderType providerType, string? serverUrl)
    {
        var pullRequest = DevOpsReferenceParser.ParsePullRequestUrl(providerType, url, serverUrl);

        Assert.Equal("acme", pullRequest.Owner);
        Assert.Equal("widgets", pullRequest.Repository);
        Assert.Equal(1, pullRequest.Number);
    }

    [Fact]
    public void ParseRepositoryUrl_rejects_issue_url_where_repository_url_expected()
    {
        Assert.Throws<ArgumentException>(() => DevOpsReferenceParser.ParseRepositoryUrl(
            DevOpsProviderType.Gitea,
            "https://gitea.example/acme/widgets/issues/1",
            "https://gitea.example"));
    }

    [Fact]
    public void ParseRepositoryUrl_rejects_gitea_url_outside_configured_server()
    {
        Assert.Throws<ArgumentException>(() => DevOpsReferenceParser.ParseRepositoryUrl(
            DevOpsProviderType.Gitea,
            "https://other.example/acme/widgets",
            "https://gitea.example"));
    }

    [Fact]
    public async Task CreateGiteaIntegrationAsync_stores_server_token_and_webhook_secret()
    {
        var service = CreateService();

        var integration = await service.CreateGiteaIntegrationAsync(
            new CreateGiteaIntegrationRequest("Gitea Prod", "https://gitea.example/", "token", "secret"),
            RequestBaseUri,
            CancellationToken.None);

        Assert.Equal("Gitea", integration.ProviderType);
        Assert.Equal("Gitea Prod", integration.DisplayName);
        Assert.Equal("https://gitea.example/", integration.ServerUrl);
        Assert.Equal("https://formicae.example/api/webhooks/gitea", integration.WebhookUrl);
        Assert.Equal("secret", integration.WebhookSecret);
        Assert.Contains("pull_request", integration.SetupInstructions.RequiredWebhookEvents);
    }

    [Fact]
    public async Task UpdateGiteaIntegrationAsync_updates_token_server_and_secret()
    {
        var service = CreateService();
        var integration = await service.CreateGiteaIntegrationAsync(
            new CreateGiteaIntegrationRequest("Gitea", "https://gitea.example", "token", "secret"),
            RequestBaseUri,
            CancellationToken.None);

        var updated = await service.UpdateGiteaIntegrationAsync(
            integration.Id,
            new UpdateGiteaIntegrationRequest("Gitea Dev", "https://git.example", "new-token", "new-secret"),
            RequestBaseUri,
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("Gitea Dev", updated.DisplayName);
        Assert.Equal("https://git.example/", updated.ServerUrl);
        Assert.Equal("new-secret", updated.WebhookSecret);
    }


    [Fact]
    public async Task AddRepositoryAsync_requires_github_app_installation()
    {
        var service = CreateService();
        var integration = await service.CreateGitHubIntegrationAsync(
            new CreateGitHubIntegrationRequest("GitHub", "client-id", "client-secret-ref", ValidPrivateKey, null),
            RequestBaseUri,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddRepositoryAsync(
            integration.Id,
            new AddConnectedRepositoryRequest("https://github.com/acme/widgets", "main", null, null),
            CancellationToken.None));

        Assert.Contains("GitHub App installation", exception.Message);
    }
    [Fact]
    public async Task AddRepositoryAsync_rejects_duplicates()
    {
        var service = CreateService();
        var integration = await service.CreateGitHubIntegrationAsync(
            new CreateGitHubIntegrationRequest("GitHub", "client-id", "client-secret-ref", ValidPrivateKey, null),
            RequestBaseUri,
            CancellationToken.None);

        var request = new AddConnectedRepositoryRequest("https://github.com/acme/widgets", "main", 123, "acme");
        await service.AddRepositoryAsync(integration.Id, request, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddRepositoryAsync(integration.Id, request, CancellationToken.None));
    }

    [Fact]
    public async Task AddRepositoryAsync_accepts_gitea_repository_without_installation()
    {
        var service = CreateService();
        var integration = await service.CreateGiteaIntegrationAsync(
            new CreateGiteaIntegrationRequest("Gitea", "https://gitea.example", "token", "secret"),
            RequestBaseUri,
            CancellationToken.None);

        var repository = await service.AddRepositoryAsync(
            integration.Id,
            new AddConnectedRepositoryRequest("https://gitea.example/acme/widgets.git", "main", null, null),
            CancellationToken.None);

        Assert.NotNull(repository);
        Assert.Equal("https://gitea.example/acme/widgets", repository.RepositoryUrl);
        Assert.Null(repository.InstallationId);
    }

    [Fact]
    public async Task AddRepositoryAsync_rejects_gitea_repository_outside_configured_server()
    {
        var service = CreateService();
        var integration = await service.CreateGiteaIntegrationAsync(
            new CreateGiteaIntegrationRequest("Gitea", "https://gitea.example", "token", "secret"),
            RequestBaseUri,
            CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() => service.AddRepositoryAsync(
            integration.Id,
            new AddConnectedRepositoryRequest("https://other.example/acme/widgets", "main", null, null),
            CancellationToken.None));
    }

    [Fact]
    public async Task DeleteRepositoryAsync_removes_connected_repository()
    {
        var service = CreateService();
        var integration = await service.CreateGitHubIntegrationAsync(
            new CreateGitHubIntegrationRequest("GitHub", "client-id", "client-secret-ref", ValidPrivateKey, null),
            RequestBaseUri,
            CancellationToken.None);
        var repository = await service.AddRepositoryAsync(
            integration.Id,
            new AddConnectedRepositoryRequest("https://github.com/acme/widgets", "main", 123, "acme"),
            CancellationToken.None);

        var removed = await service.DeleteRepositoryAsync(integration.Id, repository!.Id, CancellationToken.None);
        var repositories = await service.ListRepositoriesAsync(integration.Id, CancellationToken.None);

        Assert.True(removed);
        Assert.Empty(repositories!);
    }

    [Fact]
    public async Task DeleteAsync_removes_integration_and_repositories()
    {
        var service = CreateService();
        var integration = await service.CreateGitHubIntegrationAsync(
            new CreateGitHubIntegrationRequest("GitHub", "client-id", "client-secret-ref", ValidPrivateKey, null),
            RequestBaseUri,
            CancellationToken.None);
        await service.AddRepositoryAsync(
            integration.Id,
            new AddConnectedRepositoryRequest("https://github.com/acme/widgets", "main", 123, "acme"),
            CancellationToken.None);

        var removed = await service.DeleteAsync(integration.Id, CancellationToken.None);
        var deletedIntegration = await service.GetAsync(integration.Id, RequestBaseUri, CancellationToken.None);
        var repositories = await service.ListRepositoriesAsync(integration.Id, CancellationToken.None);

        Assert.True(removed);
        Assert.Null(deletedIntegration);
        Assert.Null(repositories);
    }

    [Fact]
    public async Task MarkIdentityProviderRestartedAsync_clears_restart_flag()
    {
        var service = CreateService();
        var integration = await service.CreateGitHubIntegrationAsync(
            new CreateGitHubIntegrationRequest("GitHub", "client-id", "client-secret-ref", ValidPrivateKey, null),
            RequestBaseUri,
            CancellationToken.None);
        integration = await service.SetIdentityProviderEnabledAsync(integration.Id, true, RequestBaseUri, CancellationToken.None);

        var restarted = await service.MarkIdentityProviderRestartedAsync(integration!.Id, RequestBaseUri, CancellationToken.None);

        Assert.NotNull(restarted);
        Assert.True(restarted.IdentityProviderEnabled);
        Assert.False(restarted.RequiresRestart);
    }

    private static DevOpsIntegrationService CreateService()
        => new(new InMemoryDevOpsIntegrationStore(), new FixedClock());

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 6, 26, 14, 0, 0, TimeSpan.Zero);
    }
}
