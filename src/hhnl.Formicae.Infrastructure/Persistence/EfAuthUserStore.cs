using hhnl.Formicae.Application.Auth;
using Microsoft.EntityFrameworkCore;

namespace hhnl.Formicae.Infrastructure.Persistence;

public sealed class EfAuthUserStore(FormicaeDbContext dbContext) : IAuthUserStore
{
    public Task<AuthUser?> GetByGitHubUserIdAsync(string gitHubUserId, CancellationToken cancellationToken)
        => dbContext.AuthUsers.SingleOrDefaultAsync(user => user.GitHubUserId == gitHubUserId, cancellationToken);

    public async Task<AuthUser> UpsertAsync(AuthUser user, CancellationToken cancellationToken)
    {
        var tracked = await dbContext.AuthUsers.SingleOrDefaultAsync(existing => existing.GitHubUserId == user.GitHubUserId, cancellationToken);
        if (tracked is null)
        {
            dbContext.AuthUsers.Add(user);
        }
        else
        {
            tracked.GitHubLogin = user.GitHubLogin;
            tracked.Email = user.Email;
            tracked.DisplayName = user.DisplayName;
            tracked.InviteCodeHash = user.InviteCodeHash;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return tracked ?? user;
    }
}
