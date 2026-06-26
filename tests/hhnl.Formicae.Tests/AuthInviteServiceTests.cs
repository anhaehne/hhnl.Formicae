using hhnl.Formicae.Application.Auth;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Tests;

public sealed class AuthInviteServiceTests
{
    [Fact]
    public async Task ValidateInviteCodeAsync_accepts_configured_invite_code()
    {
        var service = CreateService(inviteCodes: ["alpha-code"]);

        Assert.True(await service.ValidateInviteCodeAsync(" alpha-code "));
    }

    [Fact]
    public async Task ValidateInviteCodeAsync_rejects_invalid_invite_code()
    {
        var service = CreateService(inviteCodes: ["alpha-code"]);

        Assert.False(await service.ValidateInviteCodeAsync("wrong-code"));
    }

    [Fact]
    public async Task IsAllowedAsync_accepts_previously_accepted_user()
    {
        var store = new InMemoryAuthUserStore();
        var service = CreateService(store, inviteCodes: ["alpha-code"]);
        var identity = new GitHubIdentity("123", "octocat", "octocat@example.com", "Octo Cat");

        await service.AcceptInviteAsync(identity, "alpha-code", CancellationToken.None);

        Assert.True(await service.IsAllowedAsync(identity, CancellationToken.None));
        var saved = await store.GetByGitHubUserIdAsync("123", CancellationToken.None);
        Assert.NotNull(saved);
        Assert.NotEqual("alpha-code", saved.InviteCodeHash);
    }

    private static AuthInviteService CreateService(string[]? inviteCodes = null)
        => CreateService(new InMemoryAuthUserStore(), inviteCodes);

    private static AuthInviteService CreateService(InMemoryAuthUserStore store, string[]? inviteCodes = null)
        => new(
            store,
            Options.Create(new AuthOptions { InviteCodes = inviteCodes ?? [] }),
            new FixedClock(DateTimeOffset.Parse("2026-06-26T12:00:00Z")));

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
