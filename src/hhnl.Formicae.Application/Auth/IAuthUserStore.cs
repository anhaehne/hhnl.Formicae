namespace hhnl.Formicae.Application.Auth;

public interface IAuthUserStore
{
    Task<AuthUser?> GetByGitHubUserIdAsync(string gitHubUserId, CancellationToken cancellationToken);

    Task<AuthUser> UpsertAsync(AuthUser user, CancellationToken cancellationToken);
}
