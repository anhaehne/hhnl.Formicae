namespace hhnl.Formicae.Application.Auth;

public sealed class AuthOptions
{
    public bool Enabled { get; set; }

    public string Provider { get; set; } = "GitHub";

    public string CookieName { get; set; } = "formicae_auth";

    public GitHubAuthOptions GitHub { get; set; } = new();

    public string[] AllowedGitHubLogins { get; set; } = [];

    public string[] AllowedEmails { get; set; } = [];

    public string[] InviteCodes { get; set; } = [];
}

public sealed class GitHubAuthOptions
{
    public string ClientId { get; set; } = "";

    public string ClientSecret { get; set; } = "";
}
