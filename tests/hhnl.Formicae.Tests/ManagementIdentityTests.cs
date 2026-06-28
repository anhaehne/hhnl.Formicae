using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Identity;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace hhnl.Formicae.Tests;

public sealed class ManagementIdentityTests
{
    [Fact]
    public async Task CreateInviteAsync_StoresHashOnly()
    {
        await using var fixture = new IdentityFixture();
        var creator = await fixture.CreateAuthorizedUserAsync("creator");

        var invite = await fixture.Invites.CreateInviteAsync(PrincipalFor(creator), CancellationToken.None);
        var stored = await fixture.Db.InviteCodes.SingleAsync();

        Assert.False(string.IsNullOrWhiteSpace(invite.Code));
        Assert.NotEqual(invite.Code, stored.CodeHash);
        Assert.Equal(64, stored.CodeHash.Length);
        Assert.Equal(InviteService.HashCode(invite.Code!), stored.CodeHash);
    }

    [Fact]
    public async Task RedeemInviteAsync_RejectsExpiredInvite()
    {
        await using var fixture = new IdentityFixture(inviteExpiration: TimeSpan.FromMinutes(5));
        var creator = await fixture.CreateAuthorizedUserAsync("creator");
        var invite = await fixture.Invites.CreateInviteAsync(PrincipalFor(creator), CancellationToken.None);
        var redeemer = await fixture.CreateUserAsync("redeemer");

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(6);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Invites.RedeemInviteAsync(PrincipalFor(redeemer), invite.Code!, CancellationToken.None));
        Assert.Equal("Invite code has expired.", exception.Message);
    }

    [Fact]
    public async Task RedeemInviteAsync_RejectsUsedInvite()
    {
        await using var fixture = new IdentityFixture();
        var creator = await fixture.CreateAuthorizedUserAsync("creator");
        var firstRedeemer = await fixture.CreateUserAsync("first");
        var secondRedeemer = await fixture.CreateUserAsync("second");
        var invite = await fixture.Invites.CreateInviteAsync(PrincipalFor(creator), CancellationToken.None);

        await fixture.Invites.RedeemInviteAsync(PrincipalFor(firstRedeemer), invite.Code!, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Invites.RedeemInviteAsync(PrincipalFor(secondRedeemer), invite.Code!, CancellationToken.None));
        Assert.Equal("Invite code has already been used.", exception.Message);
    }

    [Fact]
    public async Task RedeemInviteAsync_GrantsAuthorizedUserRole()
    {
        await using var fixture = new IdentityFixture();
        var creator = await fixture.CreateAuthorizedUserAsync("creator");
        var redeemer = await fixture.CreateUserAsync("redeemer");
        var invite = await fixture.Invites.CreateInviteAsync(PrincipalFor(creator), CancellationToken.None);

        await fixture.Invites.RedeemInviteAsync(PrincipalFor(redeemer), invite.Code!, CancellationToken.None);

        Assert.True(await fixture.Users.IsInRoleAsync(redeemer, ManagementUserService.ManagementAdminRole));
    }

    [Fact]
    public async Task GrantHelpers_CreateMissingRoles()
    {
        await using var fixture = new IdentityFixture();
        var viewer = await fixture.CreateUserAsync("viewer");
        var operatorUser = await fixture.CreateUserAsync("operator");
        var admin = await fixture.CreateUserAsync("admin");

        await fixture.ManagementUsers.GrantViewerAsync(viewer, CancellationToken.None);
        await fixture.ManagementUsers.GrantOperatorAsync(operatorUser, CancellationToken.None);
        await fixture.ManagementUsers.GrantAdminAsync(admin, CancellationToken.None);

        Assert.True(await fixture.Roles.RoleExistsAsync(ManagementUserService.WorkflowViewerRole));
        Assert.True(await fixture.Roles.RoleExistsAsync(ManagementUserService.WorkflowOperatorRole));
        Assert.True(await fixture.Roles.RoleExistsAsync(ManagementUserService.ManagementAdminRole));
    }

    [Fact]
    public async Task AdminPermission_ImpliesOperatorAndViewer()
    {
        await using var fixture = new IdentityFixture();
        var admin = await fixture.CreateUserAsync("admin");

        await fixture.ManagementUsers.GrantAdminAsync(admin, CancellationToken.None);

        Assert.True(await fixture.ManagementUsers.IsInPermissionAsync(PrincipalFor(admin), ManagementUserService.WorkflowViewPermission));
        Assert.True(await fixture.ManagementUsers.IsInPermissionAsync(PrincipalFor(admin), ManagementUserService.WorkflowOperatePermission));
        Assert.True(await fixture.ManagementUsers.IsInPermissionAsync(PrincipalFor(admin), ManagementUserService.ManagementAdminPermission));
    }

    [Fact]
    public async Task OperatorPermission_ImpliesViewerButNotAdmin()
    {
        await using var fixture = new IdentityFixture();
        var operatorUser = await fixture.CreateUserAsync("operator-user");

        await fixture.ManagementUsers.GrantOperatorAsync(operatorUser, CancellationToken.None);

        Assert.True(await fixture.ManagementUsers.IsInPermissionAsync(PrincipalFor(operatorUser), ManagementUserService.WorkflowViewPermission));
        Assert.True(await fixture.ManagementUsers.IsInPermissionAsync(PrincipalFor(operatorUser), ManagementUserService.WorkflowOperatePermission));
        Assert.False(await fixture.ManagementUsers.IsInPermissionAsync(PrincipalFor(operatorUser), ManagementUserService.ManagementAdminPermission));
    }

    [Fact]
    public async Task ViewerPermission_DoesNotImplyOperatorOrAdmin()
    {
        await using var fixture = new IdentityFixture();
        var viewer = await fixture.CreateUserAsync("viewer");

        await fixture.ManagementUsers.GrantViewerAsync(viewer, CancellationToken.None);

        Assert.True(await fixture.ManagementUsers.IsInPermissionAsync(PrincipalFor(viewer), ManagementUserService.WorkflowViewPermission));
        Assert.False(await fixture.ManagementUsers.IsInPermissionAsync(PrincipalFor(viewer), ManagementUserService.WorkflowOperatePermission));
        Assert.False(await fixture.ManagementUsers.IsInPermissionAsync(PrincipalFor(viewer), ManagementUserService.ManagementAdminPermission));
    }

    [Fact]
    public async Task FindOrCreateExternalUserAsync_CreatesIdentityUserAndExternalLogin()
    {
        await using var fixture = new IdentityFixture();

        var user = await fixture.ManagementUsers.FindOrCreateExternalUserAsync(new ExternalUserProfile(
            "GitHub",
            "123",
            "GitHub",
            "octo",
            "Octo Cat",
            "octo@example.test"), CancellationToken.None);

        var loginUser = await fixture.Users.FindByLoginAsync("GitHub", "123");
        Assert.Equal(user.Id, loginUser?.Id);
        Assert.Equal("Octo Cat", user.DisplayName);
        Assert.Equal("octo@example.test", user.Email);
        Assert.Equal(fixture.Clock.UtcNow, user.LastLoginAt);
    }

    [Fact]
    public async Task FindOrCreateExternalUserAsync_UpdatesExistingIdentityUser()
    {
        await using var fixture = new IdentityFixture();
        var user = await fixture.ManagementUsers.FindOrCreateExternalUserAsync(new ExternalUserProfile(
            "GitHub",
            "123",
            "GitHub",
            "octo",
            "Octo Cat",
            "octo@example.test"), CancellationToken.None);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddHours(1);

        var updated = await fixture.ManagementUsers.FindOrCreateExternalUserAsync(new ExternalUserProfile(
            "GitHub",
            "123",
            "GitHub",
            "octo",
            "Mona",
            "mona@example.test"), CancellationToken.None);

        Assert.Equal(user.Id, updated.Id);
        Assert.Equal("Mona", updated.DisplayName);
        Assert.Equal("mona@example.test", updated.Email);
        Assert.Equal(fixture.Clock.UtcNow, updated.LastLoginAt);
        Assert.Single(await fixture.Users.GetLoginsAsync(updated));
    }

    private static ClaimsPrincipal PrincipalFor(FormicaeUser user)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id)], IdentityConstants.ApplicationScheme));

    private sealed class IdentityFixture : IAsyncDisposable
    {
        private readonly ServiceProvider serviceProvider;
        private readonly AsyncServiceScope scope;

        public IdentityFixture(TimeSpan? inviteExpiration = null)
        {
            Clock = new MutableClock(new DateTimeOffset(2026, 6, 26, 12, 0, 0, TimeSpan.Zero));

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.AddDbContext<FormicaeDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
            services
                .AddIdentityCore<FormicaeUser>()
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<FormicaeDbContext>()
                .AddSignInManager();
            services.AddSingleton<IClock>(Clock);
            services.Configure<ManagementAuthOptions>(options =>
            {
                options.Enabled = true;
                options.InviteCodeExpiration = inviteExpiration ?? TimeSpan.FromDays(7);
            });
            services.AddScoped<ManagementUserService>();
            services.AddScoped<InviteService>();

            serviceProvider = services.BuildServiceProvider();
            scope = serviceProvider.CreateAsyncScope();

            Db = scope.ServiceProvider.GetRequiredService<FormicaeDbContext>();
            Users = scope.ServiceProvider.GetRequiredService<UserManager<FormicaeUser>>();
            Roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            ManagementUsers = scope.ServiceProvider.GetRequiredService<ManagementUserService>();
            Invites = scope.ServiceProvider.GetRequiredService<InviteService>();
        }

        public MutableClock Clock { get; }
        public FormicaeDbContext Db { get; }
        public UserManager<FormicaeUser> Users { get; }
        public RoleManager<IdentityRole> Roles { get; }
        public ManagementUserService ManagementUsers { get; }
        public InviteService Invites { get; }

        public async Task<FormicaeUser> CreateUserAsync(string userName)
        {
            var user = new FormicaeUser
            {
                UserName = userName,
                CreatedAt = Clock.UtcNow,
                UpdatedAt = Clock.UtcNow
            };
            var result = await Users.CreateAsync(user);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
            return user;
        }

        public async Task<FormicaeUser> CreateAuthorizedUserAsync(string userName)
        {
            var user = await CreateUserAsync(userName);
            await ManagementUsers.GrantAdminAsync(user, CancellationToken.None);
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await serviceProvider.DisposeAsync();
        }
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
