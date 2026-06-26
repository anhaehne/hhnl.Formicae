namespace hhnl.Formicae.Infrastructure.Identity;

public sealed class ManagementAuthOptions
{
    public bool Enabled { get; set; }
    public TimeSpan InviteCodeExpiration { get; set; } = TimeSpan.FromDays(7);
    public bool BypassForLocalDevelopment { get; set; }
}
