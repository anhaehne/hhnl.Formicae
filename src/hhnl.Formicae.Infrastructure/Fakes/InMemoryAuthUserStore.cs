using hhnl.Formicae.Application.Auth;
using System.Collections.Concurrent;

namespace hhnl.Formicae.Infrastructure.Fakes;

public sealed class InMemoryAuthUserStore : IAuthUserStore
{
    private readonly ConcurrentDictionary<string, AuthUser> usersByGitHubId = new(StringComparer.Ordinal);

    public Task<AuthUser?> GetByGitHubUserIdAsync(string gitHubUserId, CancellationToken cancellationToken)
    {
        usersByGitHubId.TryGetValue(gitHubUserId, out var user);
        return Task.FromResult(user);
    }

    public Task<AuthUser> UpsertAsync(AuthUser user, CancellationToken cancellationToken)
    {
        usersByGitHubId.AddOrUpdate(user.GitHubUserId, user, (_, _) => user);
        return Task.FromResult(user);
    }
}
