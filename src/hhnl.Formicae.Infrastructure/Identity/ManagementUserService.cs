using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace hhnl.Formicae.Infrastructure.Identity;

public sealed class ManagementUserService(
    UserManager<FormicaeUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IClock clock)
{
    public const string WorkflowViewerRole = "WorkflowViewer";
    public const string WorkflowOperatorRole = "WorkflowOperator";
    public const string ManagementAdminRole = "ManagementAdmin";
    public const string AuthorizedUserRole = ManagementAdminRole;

    public const string WorkflowViewPermission = "WorkflowView";
    public const string WorkflowOperatePermission = "WorkflowOperate";
    public const string ManagementAdminPermission = "ManagementAdmin";

    public static readonly string[] KnownRoles =
    [
        WorkflowViewerRole,
        WorkflowOperatorRole,
        ManagementAdminRole
    ];

    public static readonly ManagementRoleDefinition[] RoleDefinitions =
    [
        new(
            WorkflowViewerRole,
            "Read workflow history, run details, comments, logs, and signals.",
            [WorkflowViewPermission]),
        new(
            WorkflowOperatorRole,
            "Start workflows and retry failed workflow or task runs.",
            [WorkflowViewPermission, WorkflowOperatePermission]),
        new(
            ManagementAdminRole,
            "Manage integrations, repositories, AI settings, invites, users, and roles.",
            [WorkflowViewPermission, WorkflowOperatePermission, ManagementAdminPermission])
    ];

    public async Task<FormicaeUser> FindOrCreateExternalUserAsync(ExternalUserProfile profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var user = await userManager.FindByLoginAsync(profile.ProviderName, profile.ProviderKey);
        if (user is null && !string.IsNullOrWhiteSpace(profile.Email))
        {
            user = await userManager.FindByEmailAsync(profile.Email);
        }

        var now = clock.UtcNow;
        if (user is null)
        {
            user = new FormicaeUser
            {
                UserName = BuildUserName(profile),
                Email = profile.Email,
                EmailConfirmed = !string.IsNullOrWhiteSpace(profile.Email),
                DisplayName = profile.DisplayName,
                CreatedAt = now,
                UpdatedAt = now,
                LastLoginAt = now
            };

            await EnsureSucceededAsync(userManager.CreateAsync(user), "create management user");
        }
        else
        {
            user.Email = string.IsNullOrWhiteSpace(profile.Email) ? user.Email : profile.Email;
            user.EmailConfirmed = user.EmailConfirmed || !string.IsNullOrWhiteSpace(profile.Email);
            user.DisplayName = profile.DisplayName;
            user.LastLoginAt = now;
            user.UpdatedAt = now;
            await EnsureSucceededAsync(userManager.UpdateAsync(user), "update management user");
        }

        if (await userManager.FindByLoginAsync(profile.ProviderName, profile.ProviderKey) is null)
        {
            await EnsureSucceededAsync(
                userManager.AddLoginAsync(user, new UserLoginInfo(profile.ProviderName, profile.ProviderKey, profile.ProviderDisplayName)),
                "link external login");
        }

        return user;
    }

    public async Task<bool> IsAuthorizedAsync(ClaimsPrincipal principal)
        => await IsInPermissionAsync(principal, WorkflowViewPermission);

    public async Task<bool> IsInPermissionAsync(ClaimsPrincipal principal, string permission)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return false;
        }

        return permission switch
        {
            WorkflowViewPermission => await userManager.IsInRoleAsync(user, WorkflowViewerRole)
                || await userManager.IsInRoleAsync(user, WorkflowOperatorRole)
                || await userManager.IsInRoleAsync(user, ManagementAdminRole),
            WorkflowOperatePermission => await userManager.IsInRoleAsync(user, WorkflowOperatorRole)
                || await userManager.IsInRoleAsync(user, ManagementAdminRole),
            ManagementAdminPermission => await userManager.IsInRoleAsync(user, ManagementAdminRole),
            _ => false
        };
    }

    public async Task GrantAuthorizedUserAsync(FormicaeUser user, CancellationToken cancellationToken)
        => await GrantAdminAsync(user, cancellationToken);

    public async Task GrantViewerAsync(FormicaeUser user, CancellationToken cancellationToken)
        => await GrantRoleAsync(user, WorkflowViewerRole, "grant workflow viewer permission", cancellationToken);

    public async Task GrantOperatorAsync(FormicaeUser user, CancellationToken cancellationToken)
        => await GrantRoleAsync(user, WorkflowOperatorRole, "grant workflow operator permission", cancellationToken);

    public async Task GrantAdminAsync(FormicaeUser user, CancellationToken cancellationToken)
        => await GrantRoleAsync(user, ManagementAdminRole, "grant management admin permission", cancellationToken);

    public async Task<FormicaeUser?> GetCurrentUserAsync(ClaimsPrincipal principal)
        => await userManager.GetUserAsync(principal);

    public async Task<IReadOnlyList<ManagementUserSummary>> ListUsersAsync(CancellationToken cancellationToken)
    {
        var users = await userManager.Users
            .OrderBy(user => user.UserName)
            .ToListAsync(cancellationToken);
        var summaries = new List<ManagementUserSummary>(users.Count);

        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            summaries.Add(await ToSummaryAsync(user));
        }

        return summaries;
    }

    public async Task<ManagementUserSummary?> GetUserSummaryAsync(string userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = await userManager.FindByIdAsync(userId);
        return user is null ? null : await ToSummaryAsync(user);
    }

    public async Task<ManagementUserSummary?> UpdateRolesAsync(string userId, IReadOnlyCollection<string> roles, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return null;
        }

        var requestedRoles = roles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var unknownRoles = requestedRoles.Except(KnownRoles, StringComparer.Ordinal).ToArray();
        if (unknownRoles.Length > 0)
        {
            throw new ArgumentException($"Unknown management roles: {string.Join(", ", unknownRoles)}.", nameof(roles));
        }

        foreach (var role in KnownRoles)
        {
            await EnsureRoleAsync(role);
        }

        var currentRoles = await userManager.GetRolesAsync(user);
        var rolesToRemove = currentRoles.Intersect(KnownRoles, StringComparer.Ordinal).Except(requestedRoles, StringComparer.Ordinal).ToArray();
        var rolesToAdd = requestedRoles.Except(currentRoles, StringComparer.Ordinal).ToArray();
        if (rolesToRemove.Length > 0)
        {
            await EnsureSucceededAsync(userManager.RemoveFromRolesAsync(user, rolesToRemove), "remove management roles");
        }

        if (rolesToAdd.Length > 0)
        {
            await EnsureSucceededAsync(userManager.AddToRolesAsync(user, rolesToAdd), "add management roles");
        }

        user.UpdatedAt = clock.UtcNow;
        await EnsureSucceededAsync(userManager.UpdateAsync(user), "update management user");

        return await ToSummaryAsync(user);
    }

    private async Task GrantRoleAsync(FormicaeUser user, string role, string action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureRoleAsync(role);
        if (!await userManager.IsInRoleAsync(user, role))
        {
            await EnsureSucceededAsync(userManager.AddToRoleAsync(user, role), action);
        }
    }

    private async Task EnsureRoleAsync(string role)
    {
        if (await roleManager.RoleExistsAsync(role))
        {
            return;
        }

        await EnsureSucceededAsync(roleManager.CreateAsync(new IdentityRole(role)), $"create {role} role");
    }

    private async Task<ManagementUserSummary> ToSummaryAsync(FormicaeUser user)
    {
        var roles = (await userManager.GetRolesAsync(user))
            .Where(role => KnownRoles.Contains(role, StringComparer.Ordinal))
            .OrderBy(role => Array.IndexOf(KnownRoles, role))
            .ToArray();
        var logins = await userManager.GetLoginsAsync(user);
        var permissions = ResolvePermissions(roles);

        return new ManagementUserSummary(
            user.Id,
            user.UserName,
            user.DisplayName,
            user.Email,
            logins.FirstOrDefault()?.LoginProvider,
            roles,
            permissions,
            user.CreatedAt,
            user.UpdatedAt,
            user.LastLoginAt);
    }

    private static IReadOnlyList<string> ResolvePermissions(IReadOnlyCollection<string> roles)
    {
        if (roles.Contains(ManagementAdminRole, StringComparer.Ordinal))
        {
            return [WorkflowViewPermission, WorkflowOperatePermission, ManagementAdminPermission];
        }

        if (roles.Contains(WorkflowOperatorRole, StringComparer.Ordinal))
        {
            return [WorkflowViewPermission, WorkflowOperatePermission];
        }

        if (roles.Contains(WorkflowViewerRole, StringComparer.Ordinal))
        {
            return [WorkflowViewPermission];
        }

        return [];
    }

    private static string BuildUserName(ExternalUserProfile profile)
    {
        var suffix = string.IsNullOrWhiteSpace(profile.UserName) ? profile.ProviderKey : profile.UserName;
        var value = $"{profile.ProviderName}{suffix}";
        var builder = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrWhiteSpace(builder) ? $"user{profile.ProviderKey}" : builder.ToLowerInvariant();
    }

    private static async Task EnsureSucceededAsync(Task<IdentityResult> operation, string action)
    {
        var result = await operation;
        if (result.Succeeded)
        {
            return;
        }

        var errors = string.Join("; ", result.Errors.Select(error => error.Description));
        throw new InvalidOperationException($"Could not {action}: {errors}");
    }
}

public sealed record ExternalUserProfile(
    string ProviderName,
    string ProviderKey,
    string ProviderDisplayName,
    string? UserName,
    string? DisplayName,
    string? Email);

public sealed record ManagementRoleDefinition(
    string Name,
    string Description,
    IReadOnlyList<string> Permissions);

public sealed record ManagementUserSummary(
    string Id,
    string? UserName,
    string? DisplayName,
    string? Email,
    string? Provider,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastLoginAt);
