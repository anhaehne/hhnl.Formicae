using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Infrastructure.Identity;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace hhnl.Formicae.Tests;

public sealed class ManagementAuthApiTests
{
    [Fact]
    public async Task AnonymousMutatingRequest_Returns401_WhenAuthEnabled()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);
        var response = await factory.CreateClient().PutAsJsonAsync("/api/ai-settings", ValidAiSettings());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedUnauthorizedMutatingRequest_Returns403()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);
        var user = await factory.CreateUserAsync("unauthorized");
        var client = factory.CreateAuthenticatedClient(user.Id);

        var response = await client.PutAsJsonAsync("/api/ai-settings", ValidAiSettings());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AuthorizedUser_CanMutate()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);
        var user = await factory.CreateAuthorizedUserAsync("authorized");
        var client = factory.CreateAuthenticatedClient(user.Id);

        var response = await client.PutAsJsonAsync("/api/ai-settings", ValidAiSettings());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuthDisabled_PermitsLocalMutatingRequest()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: false);
        var response = await factory.CreateClient().PutAsJsonAsync("/api/ai-settings", ValidAiSettings());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task EnablingIdentityProvider_GrantsCurrentUserAuthorization()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);
        var user = await factory.CreateUserAsync("bootstrap");
        var integration = await factory.CreateGitHubIntegrationAsync();
        var client = factory.CreateAuthenticatedClient(user.Id);

        var response = await client.PutAsJsonAsync($"/api/integrations/{integration.Id}/identity-provider", new { enabled = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await factory.IsAuthorizedAsync(user));
    }

    [Fact]
    public async Task InviteRedemption_AuthorizesSecondExternalUser()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);
        var creator = await factory.CreateAuthorizedUserAsync("creator");
        var redeemer = await factory.CreateUserAsync("redeemer");
        var creatorClient = factory.CreateAuthenticatedClient(creator.Id);
        var redeemClient = factory.CreateAuthenticatedClient(redeemer.Id);

        var inviteResponse = await creatorClient.PostAsync("/api/auth/invites", null);
        var invite = await inviteResponse.Content.ReadFromJsonAsync<InviteResponse>();
        var redeemResponse = await redeemClient.PostAsJsonAsync("/api/auth/invites/redeem", new { code = invite!.Code });

        Assert.Equal(HttpStatusCode.NoContent, redeemResponse.StatusCode);
        Assert.True(await factory.IsAuthorizedAsync(redeemer));
    }

    private static object ValidAiSettings()
        => new
        {
            provider = "OpenAI",
            model = "gpt-test",
            endpointUrl = (string?)null,
            authMethod = "ApiKey",
            llmApiKeySecretName = "llm-api-key"
        };

    private sealed record InviteResponse(string Code);

    private sealed class FormicaeApiFactory(bool managementAuthEnabled) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["UseFakeAdapters"] = "true",
                    ["ManagementAuth:Enabled"] = managementAuthEnabled.ToString(),
                    ["ManagementAuth:BypassForLocalDevelopment"] = "false"
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services
                    .AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            });
        }

        public HttpClient CreateAuthenticatedClient(string userId)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId);
            return client;
        }

        public async Task<FormicaeUser> CreateUserAsync(string userName)
        {
            using var scope = Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<FormicaeUser>>();
            var user = new FormicaeUser
            {
                UserName = userName,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            var result = await users.CreateAsync(user);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
            return user;
        }

        public async Task<FormicaeUser> CreateAuthorizedUserAsync(string userName)
        {
            using var scope = Services.CreateScope();
            var identityUsers = scope.ServiceProvider.GetRequiredService<UserManager<FormicaeUser>>();
            var user = new FormicaeUser
            {
                UserName = userName,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            var result = await identityUsers.CreateAsync(user);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));

            var users = scope.ServiceProvider.GetRequiredService<ManagementUserService>();
            await users.GrantAuthorizedUserAsync(user, CancellationToken.None);
            return user;
        }

        public async Task<DevOpsIntegration> CreateGitHubIntegrationAsync()
        {
            using var scope = Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDevOpsIntegrationStore>();
            return await store.CreateAsync(new DevOpsIntegration
            {
                ProviderType = DevOpsProviderType.GitHub,
                DisplayName = "GitHub",
                GitHubAppClientId = "client-id",
                GitHubAppClientSecretReference = "client-secret",
                WebhookSecret = "webhook-secret",
                WebhookUrl = "https://formicae.example/api/webhooks/github",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }, CancellationToken.None);
        }

        public async Task<bool> IsAuthorizedAsync(FormicaeUser user)
        {
            using var scope = Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<FormicaeUser>>();
            return await users.IsInRoleAsync(user, ManagementUserService.AuthorizedUserRole);
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";
        public const string UserIdHeader = "X-Test-UserId";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var userId = Request.Headers[UserIdHeader].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
