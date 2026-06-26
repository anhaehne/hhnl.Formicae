using System.Net;
using System.Net.Http.Json;

namespace hhnl.Formicae.Tests;

[Collection(ApiFactoryCollection.Name)]
public sealed class AuthApiTests
{
    [Fact]
    public async Task Protected_workflow_start_allows_anonymous_when_auth_disabled()
    {
        await using var factory = new FormicaeApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workflows/github-issue", new
        {
            issueUrl = $"https://github.com/acme/widgets/issues/{Guid.NewGuid():N}",
            repositoryUrl = "https://github.com/acme/widgets",
            baseBranch = "main",
            model = "test-model"
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Protected_workflow_start_rejects_anonymous_when_auth_enabled()
    {
        await using var factory = CreateAuthEnabledFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.PostAsJsonAsync("/api/workflows/github-issue", new
        {
            issueUrl = $"https://github.com/acme/widgets/issues/{Guid.NewGuid():N}",
            repositoryUrl = "https://github.com/acme/widgets",
            baseBranch = "main"
        });

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Protected_ai_settings_rejects_anonymous_when_auth_enabled()
    {
        await using var factory = CreateAuthEnabledFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.PutAsJsonAsync("/api/ai-settings", new
        {
            provider = "OpenAI",
            model = "gpt-test",
            endpointUrl = (string?)null,
            authMethod = "ApiKey",
            llmApiKeySecretName = (string?)null
        });

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GitHub_webhook_still_reaches_webhook_validation_when_auth_enabled()
    {
        await using var factory = CreateAuthEnabledFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.PostAsJsonAsync("/api/webhooks/github", new { action = "opened" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_endpoint_challenges_with_GitHub_redirect_when_auth_enabled()
    {
        await using var factory = CreateAuthEnabledFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/auth/login?returnUrl=%2F");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Contains("github.com/login/oauth/authorize", response.Headers.Location.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Auth_enabled_rejects_unsupported_provider_before_checking_GitHub_credentials()
    {
        using var factory = new FormicaeApiFactory(new Dictionary<string, string?>
        {
            ["Auth:Enabled"] = "true",
            ["Auth:Provider"] = "ExampleProvider"
        });

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient(new() { AllowAutoRedirect = false }));

        Assert.Contains("Authentication provider 'ExampleProvider' is not supported.", exception.Message, StringComparison.Ordinal);
    }

    private static FormicaeApiFactory CreateAuthEnabledFactory()
        => new(new Dictionary<string, string?>
        {
            ["Auth:Enabled"] = "true",
            ["Auth:GitHub:ClientId"] = "test-client",
            ["Auth:GitHub:ClientSecret"] = "test-secret"
        });
}
