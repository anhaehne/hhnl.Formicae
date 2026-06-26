namespace hhnl.Formicae.Application.Auth;

public sealed class AuthUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string GitHubUserId { get; set; } = "";

    public string GitHubLogin { get; set; } = "";

    public string? Email { get; set; }

    public string? DisplayName { get; set; }

    public string? InviteCodeHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record GitHubIdentity(
    string GitHubUserId,
    string GitHubLogin,
    string? Email,
    string? DisplayName);

public sealed record AuthSessionResponse(
    bool AuthEnabled,
    bool Authenticated,
    bool Allowed,
    string? Login,
    string? Name,
    string? Email);

public sealed record AcceptInviteRequest(string InviteCode);
