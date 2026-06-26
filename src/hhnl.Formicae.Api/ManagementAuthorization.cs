using hhnl.Formicae.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Api;

public static class ManagementAuthorization
{
    public const string PolicyName = "ManagementAuthorized";
}

public sealed class ManagementAuthorizedRequirement : IAuthorizationRequirement;

public sealed class ManagementAuthorizedHandler(
    ManagementUserService users,
    IOptions<ManagementAuthOptions> options,
    IHostEnvironment environment) : AuthorizationHandler<ManagementAuthorizedRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ManagementAuthorizedRequirement requirement)
    {
        if (!options.Value.Enabled
            || (options.Value.BypassForLocalDevelopment && environment.IsDevelopment()))
        {
            context.Succeed(requirement);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true && await users.IsAuthorizedAsync(context.User))
        {
            context.Succeed(requirement);
        }
    }
}
