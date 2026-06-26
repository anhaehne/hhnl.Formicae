using hhnl.Formicae.Application.Workflows;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace hhnl.Formicae.Application.Auth;

public sealed class AuthInviteService(IAuthUserStore users, IOptions<AuthOptions> options, IClock clock)
{
    private readonly IAuthUserStore users = users;
    private readonly IOptions<AuthOptions> options = options;
    private readonly IClock clock = clock;

    public Task<bool> ValidateInviteCodeAsync(string? inviteCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inviteCode))
        {
            return Task.FromResult(false);
        }

        var submittedHash = HashInviteCode(inviteCode);
        var valid = ConfiguredInviteCodes()
            .Select(HashInviteCode)
            .Any(hash => FixedTimeEquals(hash, submittedHash));

        return Task.FromResult(valid);
    }

    public async Task<AuthUser> AcceptInviteAsync(GitHubIdentity identity, string? inviteCode, CancellationToken cancellationToken)
    {
        if (!await ValidateInviteCodeAsync(inviteCode, cancellationToken))
        {
            throw new ArgumentException("Invite code is invalid.", nameof(inviteCode));
        }

        var existing = await users.GetByGitHubUserIdAsync(identity.GitHubUserId, cancellationToken);
        var user = existing ?? new AuthUser
        {
            Id = Guid.NewGuid(),
            GitHubUserId = identity.GitHubUserId,
            CreatedAt = clock.UtcNow
        };

        user.GitHubLogin = identity.GitHubLogin;
        user.Email = NormalizeNullable(identity.Email);
        user.DisplayName = NormalizeNullable(identity.DisplayName);
        user.InviteCodeHash = HashInviteCode(inviteCode!);

        return await users.UpsertAsync(user, cancellationToken);
    }

    public async Task<bool> IsAllowedAsync(GitHubIdentity? identity, CancellationToken cancellationToken)
    {
        if (identity is null)
        {
            return false;
        }

        if (ContainsConfiguredValue(options.Value.AllowedGitHubLogins, identity.GitHubLogin, StringComparer.OrdinalIgnoreCase)
            || ContainsConfiguredValue(options.Value.AllowedEmails, identity.Email, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var acceptedUser = await users.GetByGitHubUserIdAsync(identity.GitHubUserId, cancellationToken);
        return acceptedUser?.InviteCodeHash is not null;
    }

    private IEnumerable<string> ConfiguredInviteCodes()
        => SplitConfiguredValues(options.Value.InviteCodes);

    private static bool ContainsConfiguredValue(string[] configuredValues, string? value, StringComparer comparer)
        => !string.IsNullOrWhiteSpace(value)
            && SplitConfiguredValues(configuredValues).Contains(value.Trim(), comparer);

    private static IEnumerable<string> SplitConfiguredValues(string[] values)
        => values.SelectMany(value => value.Split([',', ';', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    public static string HashInviteCode(string inviteCode)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inviteCode.Trim())));
}
