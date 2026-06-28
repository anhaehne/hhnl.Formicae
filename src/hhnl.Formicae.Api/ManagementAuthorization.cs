using hhnl.Formicae.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Api;

public static class ManagementAuthorization
{
    public const string WorkflowView = "WorkflowView";
    public const string WorkflowOperate = "WorkflowOperate";
    public const string ManagementAdmin = "ManagementAdmin";

    public const string PolicyName = ManagementAdmin;
    public const string ManagementAuthorized = ManagementAdmin;
}

public sealed class ManagementPermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public sealed class ManagementAuthorizedHandler(
    ManagementUserService users,
    IOptions<ManagementAuthOptions> options,
    IHostEnvironment environment) : AuthorizationHandler<ManagementPermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ManagementPermissionRequirement requirement)
    {
        if (!options.Value.Enabled
            || (options.Value.BypassForLocalDevelopment && environment.IsDevelopment()))
        {
            context.Succeed(requirement);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true && await users.IsInPermissionAsync(context.User, requirement.Permission))
        {
            context.Succeed(requirement);
        }
    }
}
