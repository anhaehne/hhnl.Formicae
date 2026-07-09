using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Infrastructure.DevOps;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Infrastructure.GitHub;
using Microsoft.Extensions.DependencyInjection;
using Octokit;

namespace hhnl.Formicae.Tests;

public sealed class DevOpsPlatformFactoryTests
{
    [Fact]
    public async Task CreateForRepositoryAsync_dispatches_github_repository_to_github_platform()
    {
        var store = new InMemoryDevOpsIntegrationStore();
        var integration = await store.CreateAsync(new DevOpsIntegration
        {
            ProviderType = DevOpsProviderType.GitHub,
            DisplayName = "GitHub",
            ServerUrl = "https://github.com",
            GitHubAppClientId = "client-id",
            WebhookSecret = "secret",
            WebhookUrl = "https://formicae.example/api/webhooks/github"
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
        var factory = new DevOpsPlatformFactory(store, CreateServices(store));

        var context = await factory.CreateForRepositoryAsync("https://github.com/acme/widgets.git", CancellationToken.None);

        Assert.Equal(DevOpsProviderType.GitHub, context.Integration.ProviderType);
        Assert.IsType<GitHubDevOpsPlatform>(context.Platform);
    }

    [Fact]
    public async Task CreateForRepositoryAsync_dispatches_gitea_repository_to_gitea_platform()
    {
        var store = new InMemoryDevOpsIntegrationStore();
        var integration = await store.CreateAsync(new DevOpsIntegration
        {
            ProviderType = DevOpsProviderType.Gitea,
            DisplayName = "Gitea",
            ServerUrl = "https://gitea.example",
            AccessToken = "token",
            WebhookSecret = "secret",
            WebhookUrl = "https://formicae.example/api/webhooks/gitea"
        }, CancellationToken.None);
        await store.AddRepositoryAsync(new ConnectedRepository
        {
            DevOpsIntegrationId = integration.Id,
            Owner = "acme",
            Name = "widgets",
            RepositoryUrl = "https://gitea.example/acme/widgets",
            DefaultBranch = "main"
        }, CancellationToken.None);
        var factory = new DevOpsPlatformFactory(store, CreateServices(store));

        var context = await factory.CreateForRepositoryAsync("https://gitea.example/acme/widgets.git", CancellationToken.None);

        Assert.Equal(DevOpsProviderType.Gitea, context.Integration.ProviderType);
        Assert.Equal(DevOpsProviderType.Gitea, context.Platform.ProviderType);
    }

    [Fact]
    public async Task CreateForRepositoryAsync_resolves_each_repository_to_its_connected_integration()
    {
        var store = new InMemoryDevOpsIntegrationStore();
        var github = await store.CreateAsync(new DevOpsIntegration
        {
            ProviderType = DevOpsProviderType.GitHub,
            DisplayName = "GitHub",
            ServerUrl = "https://github.com",
            GitHubAppClientId = "client-id",
            WebhookSecret = "secret",
            WebhookUrl = "https://formicae.example/api/webhooks/github"
        }, CancellationToken.None);
        var gitea = await store.CreateAsync(new DevOpsIntegration
        {
            ProviderType = DevOpsProviderType.Gitea,
            DisplayName = "Gitea",
            ServerUrl = "https://gitea.example",
            AccessToken = "token",
            WebhookSecret = "secret",
            WebhookUrl = "https://formicae.example/api/webhooks/gitea"
        }, CancellationToken.None);
        await store.AddRepositoryAsync(new ConnectedRepository
        {
            DevOpsIntegrationId = github.Id,
            Owner = "acme",
            Name = "widgets",
            RepositoryUrl = "https://github.com/acme/widgets",
            DefaultBranch = "main",
            InstallationId = 123
        }, CancellationToken.None);
        await store.AddRepositoryAsync(new ConnectedRepository
        {
            DevOpsIntegrationId = gitea.Id,
            Owner = "acme",
            Name = "tools",
            RepositoryUrl = "https://gitea.example/acme/tools",
            DefaultBranch = "develop"
        }, CancellationToken.None);
        var factory = new DevOpsPlatformFactory(store, CreateServices(store));

        var githubContext = await factory.CreateForRepositoryAsync("https://github.com/acme/widgets.git", CancellationToken.None);
        var giteaContext = await factory.CreateForRepositoryAsync("https://gitea.example/acme/tools.git", CancellationToken.None);

        Assert.Equal(github.Id, githubContext.Integration.Id);
        Assert.Equal("https://github.com/acme/widgets", githubContext.ConnectedRepository.RepositoryUrl);
        Assert.Equal(DevOpsProviderType.GitHub, githubContext.Platform.ProviderType);
        Assert.Equal(gitea.Id, giteaContext.Integration.Id);
        Assert.Equal("https://gitea.example/acme/tools", giteaContext.ConnectedRepository.RepositoryUrl);
        Assert.Equal(DevOpsProviderType.Gitea, giteaContext.Platform.ProviderType);
    }

    [Fact]
    public async Task CreateForRepositoryAsync_rejects_unknown_repository()
    {
        var store = new InMemoryDevOpsIntegrationStore();
        var factory = new DevOpsPlatformFactory(store, CreateServices(store));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.CreateForRepositoryAsync("https://github.com/acme/widgets", CancellationToken.None));

        Assert.Contains("not connected", exception.Message);
    }

    private static IServiceProvider CreateServices(InMemoryDevOpsIntegrationStore store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton<IDevOpsIntegrationStore>(store);
        services.AddSingleton<IGitHubClientFactory>(new StaticGitHubClientFactory());
        services.AddTransient<GitHubDevOpsPlatform>();
        return services.BuildServiceProvider();
    }

    private sealed class StaticGitHubClientFactory : IGitHubClientFactory
    {
        public GitHubClient CreateClient(bool requireToken)
            => new(new ProductHeaderValue("hhnl-formicae-test"));

        public Task<GitHubClient> CreateClientForRepositoryAsync(string repositoryUrl, CancellationToken cancellationToken)
            => Task.FromResult(new GitHubClient(new ProductHeaderValue("hhnl-formicae-test")));
    }
}
