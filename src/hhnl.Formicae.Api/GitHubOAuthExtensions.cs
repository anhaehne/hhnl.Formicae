using hhnl.Formicae.Application.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;

namespace hhnl.Formicae.Api;

public static class GitHubOAuthExtensions
{
    public static void ValidateFormicaeOAuthProvider(AuthOptions authOptions)
    {
        if (string.Equals(authOptions.Provider, FormicaeAuth.GitHubScheme, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(authOptions.GitHub.ClientId) || string.IsNullOrWhiteSpace(authOptions.GitHub.ClientSecret))
            {
                throw new InvalidOperationException("Auth:GitHub:ClientId and Auth:GitHub:ClientSecret are required when Auth:Enabled=true and Auth:Provider=GitHub.");
            }

            return;
        }

        throw new InvalidOperationException($"Authentication provider '{authOptions.Provider}' is not supported.");
    }

    public static AuthenticationBuilder AddFormicaeOAuthProvider(
        this AuthenticationBuilder builder,
        AuthOptions authOptions)
    {
        if (string.Equals(authOptions.Provider, FormicaeAuth.GitHubScheme, StringComparison.OrdinalIgnoreCase))
        {
            return builder.AddGitHubOAuth(authOptions);
        }

        throw new InvalidOperationException($"Authentication provider '{authOptions.Provider}' is not supported.");
    }

    private static AuthenticationBuilder AddGitHubOAuth(this AuthenticationBuilder builder, AuthOptions authOptions)
        => builder.AddOAuth(FormicaeAuth.GitHubScheme, options =>
        {
            options.ClientId = authOptions.GitHub.ClientId;
            options.ClientSecret = authOptions.GitHub.ClientSecret;
            options.CallbackPath = "/signin-github";
            options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
            options.TokenEndpoint = "https://github.com/login/oauth/access_token";
            options.UserInformationEndpoint = "https://api.github.com/user";
            options.Scope.Add("read:user");
            options.Scope.Add("user:email");
            options.SaveTokens = false;
            options.Events = new OAuthEvents
            {
                OnRedirectToAuthorizationEndpoint = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                        && !context.Request.Path.StartsWithSegments("/api/auth/login", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                },
                OnCreatingTicket = async context =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                    request.Headers.UserAgent.ParseAdd("hhnl-formicae");
                    using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                    response.EnsureSuccessStatusCode();

                    using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
                    var root = payload.RootElement;
                    var id = root.GetProperty("id").GetInt64().ToString();
                    var login = root.GetProperty("login").GetString() ?? "";
                    var name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    var email = root.TryGetProperty("email", out var emailElement) ? emailElement.GetString() : null;

                    context.Identity?.AddClaim(new Claim(FormicaeAuth.GitHubUserIdClaim, id));
                    context.Identity?.AddClaim(new Claim(ClaimTypes.NameIdentifier, id));
                    context.Identity?.AddClaim(new Claim(FormicaeAuth.GitHubLoginClaim, login));
                    context.Identity?.AddClaim(new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(name) ? login : name));
                    if (!string.IsNullOrWhiteSpace(email))
                    {
                        context.Identity?.AddClaim(new Claim(ClaimTypes.Email, email));
                    }
                }
            };
        });
}
