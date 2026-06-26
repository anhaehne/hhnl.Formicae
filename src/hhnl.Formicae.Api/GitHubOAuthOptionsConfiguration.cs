using hhnl.Formicae.Application.Integrations;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Options;

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
        options.SaveTokens = true;
        options.ClaimsIssuer = "GitHub";

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
}
