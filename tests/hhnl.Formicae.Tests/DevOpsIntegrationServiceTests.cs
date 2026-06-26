using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class DevOpsIntegrationServiceTests
{
    private static readonly Uri RequestBaseUri = new("https://formicae.example");

    [Fact]
    public async Task CreateGitHubIntegrationAsync_generates_secret_and_setup_urls()
    {
        var service = CreateService();

        var integration = await service.CreateGitHubIntegrationAsync(
            new CreateGitHubIntegrationRequest("GitHub Prod", "client-id", "client-secret-ref", null),
            RequestBaseUri,
            CancellationToken.None);

        Assert.Equal("GitHub Prod", integration.DisplayName);
        Assert.Equal("client-id", integration.GitHubAppClientId);
        Assert.Equal("https://formicae.example/api/webhooks/github", integration.WebhookUrl);
        Assert.Equal("https://formicae.example/api/auth/github/callback", integration.SetupInstructions.CallbackUrl);
        Assert.Equal(64, integration.WebhookSecret.Length);
        Assert.Contains("issues", integration.SetupInstructions.RequiredWebhookEvents);
    }

    [Theory]
    [InlineData("", "client-secret-ref")]
    [InlineData("client-id", "")]
    public async Task CreateGitHubIntegrationAsync_validates_required_fields(string clientId, string secretReference)
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateGitHubIntegrationAsync(
            new CreateGitHubIntegrationRequest("GitHub", clientId, secretReference, null),
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

    [Fact]
    public async Task AddRepositoryAsync_rejects_duplicates()
    {
        var service = CreateService();
        var integration = await service.CreateGitHubIntegrationAsync(
            new CreateGitHubIntegrationRequest("GitHub", "client-id", "client-secret-ref", null),
            RequestBaseUri,
            CancellationToken.None);

        var request = new AddConnectedRepositoryRequest("https://github.com/acme/widgets", "main", 123, "acme");
        await service.AddRepositoryAsync(integration.Id, request, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddRepositoryAsync(integration.Id, request, CancellationToken.None));
    }

    private static DevOpsIntegrationService CreateService()
        => new(new InMemoryDevOpsIntegrationStore(), new FixedClock());

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 6, 26, 14, 0, 0, TimeSpan.Zero);
    }
}
