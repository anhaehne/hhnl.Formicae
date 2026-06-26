using Microsoft.AspNetCore.Identity;

namespace hhnl.Formicae.Infrastructure.Persistence;

public sealed class FormicaeUser : IdentityUser
{
    public string? DisplayName { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
