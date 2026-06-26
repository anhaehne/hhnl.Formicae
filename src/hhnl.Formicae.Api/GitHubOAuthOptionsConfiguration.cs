using hhnl.Formicae.Application.Integrations;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Json;

namespace hhnl.Formicae.Api;

public sealed class GitHubOAuthOptionsConfiguration(IServiceScopeFactory scopeFactory, IConfiguration configuration) : IConfigureNamedOptions<OAuthOptions>
{
    public void Configure(string? name, OAuthOptions options)
    {
        if (!string.Equals(name, "GitHub", StringComparison.Ordinal))
        {
            return;
        }

        options.ClientId = configuration["Authentication:GitHub:ClientId"] ?? "not-configured";
        options.ClientSecret = configuration["Authentication:GitHub:ClientSecret"] ?? "not-configured";
        options.CallbackPath = "/api/auth/external-callback";
        options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
        options.TokenEndpoint = "https://github.com/login/oauth/access_token";
        options.UserInformationEndpoint = "https://api.github.com/user";
        options.Scope.Clear();
        options.Scope.Add("read:user");
        options.Scope.Add("user:email");
        options.Scope.Add("repo");
        options.SaveTokens = true;
        options.ClaimsIssuer = "GitHub";
        options.Events.OnCreatingTicket = async context =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd("hhnl-formicae");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.AccessToken);

            using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted);
            using var user = await JsonDocument.ParseAsync(stream, cancellationToken: context.HttpContext.RequestAborted);
            var root = user.RootElement;
            AddClaim(context, ClaimTypes.NameIdentifier, root, "id");
            AddClaim(context, ClaimTypes.Name, root, "login");
            AddClaim(context, ClaimTypes.Email, root, "email");
        };

        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevOpsIntegrationStore>();
        var integration = store.GetGitHubIdentityProviderAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (integration is not null)
        {
            options.ClientId = integration.GitHubAppClientId;
            if (!string.IsNullOrWhiteSpace(integration.GitHubAppClientSecretReference))
            {
                options.ClientSecret = integration.GitHubAppClientSecretReference;
            }
        }
    }

    public void Configure(OAuthOptions options)
        => Configure(Options.DefaultName, options);

    private static void AddClaim(OAuthCreatingTicketContext context, string claimType, JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        if (!string.IsNullOrWhiteSpace(text))
        {
            context.Identity?.AddClaim(new Claim(claimType, text));
        }
    }
}
