namespace hhnl.Formicae.Application.Management;

public sealed class InviteCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CodeHash { get; set; } = string.Empty;
    public string CreatedByUserId { get; set; } = string.Empty;
    public string? UsedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public sealed record InviteCodeResponse(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? UsedAt,
    string? Code = null);

public sealed record RedeemInviteRequest(string Code);
