using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace hhnl.Formicae.Infrastructure.Identity;

public sealed class ManagementUserService(
    UserManager<FormicaeUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IClock clock)
{
    public const string AuthorizedUserRole = "AuthorizedUser";

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
    {
        var user = await userManager.GetUserAsync(principal);
        return user is not null && await userManager.IsInRoleAsync(user, AuthorizedUserRole);
    }

    public async Task GrantAuthorizedUserAsync(FormicaeUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureAuthorizedRoleAsync();
        if (!await userManager.IsInRoleAsync(user, AuthorizedUserRole))
        {
            await EnsureSucceededAsync(userManager.AddToRoleAsync(user, AuthorizedUserRole), "grant management authorization");
        }
    }

    public async Task<FormicaeUser?> GetCurrentUserAsync(ClaimsPrincipal principal)
        => await userManager.GetUserAsync(principal);

    private async Task EnsureAuthorizedRoleAsync()
    {
        if (await roleManager.RoleExistsAsync(AuthorizedUserRole))
        {
            return;
        }

        await EnsureSucceededAsync(roleManager.CreateAsync(new IdentityRole(AuthorizedUserRole)), "create management authorization role");
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
