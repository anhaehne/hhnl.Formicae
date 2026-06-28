using hhnl.Formicae.Application.Management;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace hhnl.Formicae.Infrastructure.Identity;

public sealed class InviteService(
    FormicaeDbContext dbContext,
    ManagementUserService users,
    IClock clock,
    IOptions<ManagementAuthOptions> options)
{
    public async Task<InviteCodeResponse> CreateInviteAsync(ClaimsPrincipal creator, CancellationToken cancellationToken)
    {
        var currentUser = await users.GetCurrentUserAsync(creator)
            ?? throw new UnauthorizedAccessException("Authenticated user is required.");
        if (!await users.IsInPermissionAsync(creator, ManagementUserService.ManagementAdminPermission))
        {
            throw new UnauthorizedAccessException("Authorized user is required.");
        }

        var now = clock.UtcNow;
        var code = GenerateCode();
        var invite = new InviteCode
        {
            CodeHash = HashCode(code),
            CreatedByUserId = currentUser.Id,
            CreatedAt = now,
            ExpiresAt = now.Add(options.Value.InviteCodeExpiration)
        };

        dbContext.InviteCodes.Add(invite);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new InviteCodeResponse(invite.Id, invite.CreatedAt, invite.ExpiresAt, invite.UsedAt, code);
    }

    public async Task<IReadOnlyList<InviteCodeResponse>> ListInvitesAsync(ClaimsPrincipal creator, CancellationToken cancellationToken)
    {
        if (!await users.IsInPermissionAsync(creator, ManagementUserService.ManagementAdminPermission))
        {
            throw new UnauthorizedAccessException("Authorized user is required.");
        }

        return await dbContext.InviteCodes
            .AsNoTracking()
            .OrderByDescending(invite => invite.CreatedAt)
            .Select(invite => new InviteCodeResponse(invite.Id, invite.CreatedAt, invite.ExpiresAt, invite.UsedAt, null))
            .ToArrayAsync(cancellationToken);
    }

    public async Task RedeemInviteAsync(ClaimsPrincipal currentPrincipal, string code, CancellationToken cancellationToken)
    {
        var currentUser = await users.GetCurrentUserAsync(currentPrincipal)
            ?? throw new UnauthorizedAccessException("Authenticated user is required.");

        await RedeemInviteAsync(currentUser, code, cancellationToken);
    }

    public async Task RedeemInviteAsync(FormicaeUser currentUser, string code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Invite code is required.", nameof(code));
        }

        var codeHash = HashCode(code.Trim());
        var invite = await dbContext.InviteCodes
            .FirstOrDefaultAsync(invite => invite.CodeHash == codeHash, cancellationToken)
            ?? throw new InvalidOperationException("Invite code is invalid.");

        var now = clock.UtcNow;
        if (invite.UsedAt.HasValue)
        {
            throw new InvalidOperationException("Invite code has already been used.");
        }

        if (invite.ExpiresAt <= now)
        {
            throw new InvalidOperationException("Invite code has expired.");
        }

        invite.UsedByUserId = currentUser.Id;
        invite.UsedAt = now;
        await users.GrantAdminAsync(currentUser, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public static string HashCode(string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GenerateCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
