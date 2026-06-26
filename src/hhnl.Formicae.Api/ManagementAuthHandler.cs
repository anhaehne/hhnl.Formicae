using hhnl.Formicae.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace hhnl.Formicae.Api;

public static class FormicaeAuth
{
    public const string CookieScheme = "FormicaeCookie";
    public const string GitHubScheme = "GitHub";
    public const string GitHubUserIdClaim = "urn:github:id";
    public const string GitHubLoginClaim = "urn:github:login";
    public const string ManagementPolicy = "RequireManagementAuth";

    public static GitHubIdentity? GetGitHubIdentity(ClaimsPrincipal user)
    {
        var gitHubUserId = user.FindFirstValue(GitHubUserIdClaim) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        var login = user.FindFirstValue(GitHubLoginClaim) ?? user.Identity?.Name;

        if (string.IsNullOrWhiteSpace(gitHubUserId) || string.IsNullOrWhiteSpace(login))
        {
            return null;
        }

        return new GitHubIdentity(
            gitHubUserId,
            login,
            user.FindFirstValue(ClaimTypes.Email),
            user.FindFirstValue(ClaimTypes.Name));
    }
}

public sealed class ManagementAuthRequirement : IAuthorizationRequirement;

public sealed class ManagementAuthHandler(
    IOptions<AuthOptions> options,
    AuthInviteService invites) : AuthorizationHandler<ManagementAuthRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ManagementAuthRequirement requirement)
    {
        if (!options.Value.Enabled)
        {
            context.Succeed(requirement);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var identity = FormicaeAuth.GetGitHubIdentity(context.User);
        if (await invites.IsAllowedAsync(identity, CancellationToken.None))
        {
            context.Succeed(requirement);
        }
    }
}
