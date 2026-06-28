using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Identity;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace hhnl.Formicae.Tests;

public sealed class ManagementAuthApiTests
{
    [Fact]
    public async Task AnonymousMutatingRequest_Returns401_WhenAuthEnabled()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);
        var response = await factory.CreateClient().PutAsJsonAsync("/api/ai-settings", ValidAiSettings());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedUnauthorizedMutatingRequest_Returns403()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);
        var user = await factory.CreateUserAsync("unauthorized");
        var client = factory.CreateAuthenticatedClient(user.Id);

        var response = await client.PutAsJsonAsync("/api/ai-settings", ValidAiSettings());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AuthorizedUser_CanMutate()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);
        var user = await factory.CreateAdminAsync("authorized");
        var client = factory.CreateAuthenticatedClient(user.Id);

        var response = await client.PutAsJsonAsync("/api/ai-settings", ValidAiSettings());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuthDisabled_PermitsLocalMutatingRequest()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: false);
        var response = await factory.CreateClient().PutAsJsonAsync("/api/ai-settings", ValidAiSettings());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousWorkflowRead_Returns401_WhenAuthEnabled()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);

        var response = await factory.CreateClient().GetAsync("/api/workflows");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Viewer_CanReadWorkflows_ButCannotOperate()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);
        var user = await factory.CreateViewerAsync("viewer");
        var workflow = await factory.CreateFailedWorkflowAsync();
        var client = factory.CreateAuthenticatedClient(user.Id);

        var listResponse = await client.GetAsync("/api/workflows");
        var detailResponse = await client.GetAsync($"/api/workflows/{workflow.WorkflowId}");
        var runsResponse = await client.GetAsync($"/api/workflows/{workflow.WorkflowId}/runs");
        var eventsResponse = await client.GetAsync($"/api/workflows/{workflow.WorkflowId}/events");
        var signalsResponse = await client.GetAsync($"/api/workflows/{workflow.WorkflowId}/signals");
        var chatResponse = await client.GetAsync($"/api/workflows/{workflow.WorkflowId}/chat-messages");
        var logsResponse = await client.GetAsync($"/api/workflows/{workflow.WorkflowId}/logs");
        var startResponse = await client.PostAsJsonAsync("/api/workflows/github-issue", ValidStartWorkflow());
        var retryResponse = await client.PostAsync($"/api/workflows/{workflow.WorkflowId}/retry", null);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, runsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, eventsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, signalsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, chatResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, logsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, startResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, retryResponse.StatusCode);
    }

    [Fact]
    public async Task Operator_CanOperateWorkflows_ButCannotAdminister()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);
        var user = await factory.CreateOperatorAsync("operator");
        var workflow = await factory.CreateFailedWorkflowAsync();
        var taskRunWorkflow = await factory.CreateFailedWorkflowAsync();
        var client = factory.CreateAuthenticatedClient(user.Id);

        var startResponse = await client.PostAsJsonAsync("/api/workflows/github-issue", ValidStartWorkflow());
        var retryWorkflowResponse = await client.PostAsync($"/api/workflows/{workflow.WorkflowId}/retry", null);
        var taskRunResponse = await client.PostAsync($"/api/workflows/{taskRunWorkflow.WorkflowId}/runs/{taskRunWorkflow.TaskRunId}/retry", null);
        var aiSettingsResponse = await client.PutAsJsonAsync("/api/ai-settings", ValidAiSettings());
        var integrationResponse = await client.PostAsJsonAsync("/api/integrations/github", new { });

        Assert.Equal(HttpStatusCode.Accepted, startResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retryWorkflowResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, taskRunResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, aiSettingsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, integrationResponse.StatusCode);
    }

    [Fact]
    public async Task Operator_CannotEnableIdentityProvider_WhenIdentityProviderAlreadyExists()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);
        var user = await factory.CreateOperatorAsync("operator-bootstrap");
        await factory.CreateGitHubIntegrationAsync(identityProviderEnabled: true);
        var targetIntegration = await factory.CreateGitHubIntegrationAsync();
        var client = factory.CreateAuthenticatedClient(user.Id);

        var response = await client.PutAsJsonAsync($"/api/integrations/{targetIntegration.Id}/identity-provider", new { enabled = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }


    [Fact]
    public async Task Admin_CanUpdateSettings_AndCreateInvite()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);
        var user = await factory.CreateAdminAsync("admin");
        var client = factory.CreateAuthenticatedClient(user.Id);

        var settingsResponse = await client.PutAsJsonAsync("/api/ai-settings", ValidAiSettings());
        var inviteResponse = await client.PostAsync("/api/auth/invites", null);

        Assert.Equal(HttpStatusCode.OK, settingsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, inviteResponse.StatusCode);
    }

    [Fact]
    public async Task Admin_CanListRolesAndAssignUserRoles()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);
        var admin = await factory.CreateAdminAsync("roles-admin");
        var target = await factory.CreateViewerAsync("roles-target");
        var client = factory.CreateAuthenticatedClient(admin.Id);

        var rolesResponse = await client.GetAsync("/api/auth/roles");
        var usersResponse = await client.GetAsync("/api/auth/users");
        var roles = await rolesResponse.Content.ReadFromJsonAsync<List<RoleDefinitionResponse>>();
        var users = await usersResponse.Content.ReadFromJsonAsync<List<ManagementUserResponse>>();
        var updateResponse = await client.PutAsJsonAsync($"/api/auth/users/{target.Id}/roles", new
        {
            roles = new[] { ManagementUserService.WorkflowOperatorRole }
        });
        var updated = await updateResponse.Content.ReadFromJsonAsync<ManagementUserResponse>();

        Assert.Equal(HttpStatusCode.OK, rolesResponse.StatusCode);
        Assert.Contains(roles!, role => role.Name == ManagementUserService.ManagementAdminRole
            && role.Permissions.Contains(ManagementUserService.ManagementAdminPermission));
        Assert.Equal(HttpStatusCode.OK, usersResponse.StatusCode);
        Assert.Contains(users!, user => user.Id == admin.Id && user.Roles.Contains(ManagementUserService.ManagementAdminRole));
        Assert.Contains(users!, user => user.Id == target.Id && user.Roles.Contains(ManagementUserService.WorkflowViewerRole));
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.NotNull(updated);
        Assert.Equal(target.Id, updated!.Id);
        Assert.DoesNotContain(ManagementUserService.WorkflowViewerRole, updated.Roles);
        Assert.Contains(ManagementUserService.WorkflowOperatorRole, updated.Roles);
        Assert.Contains(ManagementUserService.WorkflowOperatePermission, updated.Permissions);
    }

    [Fact]
    public async Task Operator_CannotManageUsers()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);
        var operatorUser = await factory.CreateOperatorAsync("user-operator");
        var target = await factory.CreateViewerAsync("user-target");
        var client = factory.CreateAuthenticatedClient(operatorUser.Id);

        var rolesResponse = await client.GetAsync("/api/auth/roles");
        var usersResponse = await client.GetAsync("/api/auth/users");
        var updateResponse = await client.PutAsJsonAsync($"/api/auth/users/{target.Id}/roles", new
        {
            roles = new[] { ManagementUserService.WorkflowOperatorRole }
        });

        Assert.Equal(HttpStatusCode.Forbidden, rolesResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, usersResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, updateResponse.StatusCode);
    }

    [Fact]
    public async Task Admin_CannotRemoveOwnAdminRole()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);
        var admin = await factory.CreateAdminAsync("self-admin");
        var client = factory.CreateAuthenticatedClient(admin.Id);

        var response = await client.PutAsJsonAsync($"/api/auth/users/{admin.Id}/roles", new { roles = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await factory.IsAdminAsync(admin));
    }

    [Fact]
    public async Task AuthDisabled_PermitsWorkflowReadsAndOperations()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: false);
        var workflow = await factory.CreateFailedWorkflowAsync();
        var client = factory.CreateClient();

        var readResponse = await client.GetAsync("/api/workflows");
        var startResponse = await client.PostAsJsonAsync("/api/workflows/github-issue", ValidStartWorkflow());
        var retryResponse = await client.PostAsync($"/api/workflows/{workflow.WorkflowId}/retry", null);

        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, startResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
    }

    [Fact]
    public async Task EnablingIdentityProvider_RequiresAuthenticatedUser_AndDoesNotActivate()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: false);
        var integration = await factory.CreateGitHubIntegrationAsync();
        var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"/api/integrations/{integration.Id}/identity-provider", new { enabled = true });
        var stored = await factory.GetIntegrationAsync(integration.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(stored!.IdentityProviderEnabled);
    }
    [Fact]
    public async Task EnablingIdentityProvider_GrantsCurrentUserAuthorization()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);
        var user = await factory.CreateUserAsync("bootstrap");
        var integration = await factory.CreateGitHubIntegrationAsync();
        var client = factory.CreateAuthenticatedClient(user.Id);

        var response = await client.PutAsJsonAsync($"/api/integrations/{integration.Id}/identity-provider", new { enabled = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await factory.IsAdminAsync(user));
    }

    [Theory]
    [InlineData("viewer", true, true, false, false)]
    [InlineData("operator", true, true, true, false)]
    [InlineData("admin", true, true, true, true)]
    public async Task CurrentUser_ReturnsCapabilities(string role, bool authorized, bool canView, bool canTrigger, bool canAdminister)
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);
        var user = role switch
        {
            "viewer" => await factory.CreateViewerAsync("viewer-current"),
            "operator" => await factory.CreateOperatorAsync("operator-current"),
            _ => await factory.CreateAdminAsync("admin-current")
        };
        var client = factory.CreateAuthenticatedClient(user.Id);

        var currentUser = await client.GetFromJsonAsync<CurrentUserResponse>("/api/auth/current-user");

        Assert.NotNull(currentUser);
        Assert.True(currentUser!.Authenticated);
        Assert.Equal(authorized, currentUser.Authorized);
        Assert.Equal(canView, currentUser.CanViewWorkflows);
        Assert.Equal(canTrigger, currentUser.CanTriggerWorkflows);
        Assert.Equal(canAdminister, currentUser.CanAdminister);
    }


    [Fact]
    public async Task CurrentUser_ReturnsAuthRequired_WhenIdentityProviderEnabled()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);
        await factory.CreateGitHubIntegrationAsync(identityProviderEnabled: true);
        var client = factory.CreateClient();

        var currentUser = await client.GetFromJsonAsync<CurrentUserResponse>("/api/auth/current-user");

        Assert.NotNull(currentUser);
        Assert.False(currentUser!.Authenticated);
        Assert.False(currentUser.Authorized);
        Assert.True(currentUser.AuthRequired);
    }
    [Fact]
    public async Task InviteRedemption_AuthorizesSecondExternalUser()
    {
        await using var factory = new FormicaeApiFactory(managementAuthEnabled: true);
        var creator = await factory.CreateAdminAsync("creator");
        var redeemer = await factory.CreateUserAsync("redeemer");
        var creatorClient = factory.CreateAuthenticatedClient(creator.Id);
        var redeemClient = factory.CreateAuthenticatedClient(redeemer.Id);

        var inviteResponse = await creatorClient.PostAsync("/api/auth/invites", null);
        var invite = await inviteResponse.Content.ReadFromJsonAsync<InviteResponse>();
        var redeemResponse = await redeemClient.PostAsJsonAsync("/api/auth/invites/redeem", new { code = invite!.Code });

        Assert.Equal(HttpStatusCode.NoContent, redeemResponse.StatusCode);
        Assert.True(await factory.IsAdminAsync(redeemer));
    }

    private static object ValidAiSettings()
        => new
        {
            provider = "OpenAI",
            model = "gpt-test",
            endpointUrl = (string?)null,
            authMethod = "ApiKey",
            llmApiKeySecretName = "llm-api-key"
        };

    private static object ValidStartWorkflow()
        => new
        {
            issueUrl = "https://github.com/acme/widgets/issues/1",
            repositoryUrl = "https://github.com/acme/widgets",
            baseBranch = "main",
            model = (string?)null
        };

    private sealed record InviteResponse(string Code);
    private sealed record RoleDefinitionResponse(string Name, IReadOnlyList<string> Permissions);
    private sealed record ManagementUserResponse(string Id, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);
    private sealed record CurrentUserResponse(
        bool Authenticated,
        bool Authorized,
        bool AuthRequired,
        bool CanViewWorkflows,
        bool CanTriggerWorkflows,
        bool CanAdminister);
    private sealed record FailedWorkflow(Guid WorkflowId, Guid TaskRunId);

    private sealed class FormicaeApiFactory(bool managementAuthEnabled) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["UseFakeAdapters"] = "true",
                    ["ManagementAuth:Enabled"] = managementAuthEnabled.ToString(),
                    ["ManagementAuth:BypassForLocalDevelopment"] = "false"
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services
                    .AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            });
        }

        public HttpClient CreateAuthenticatedClient(string userId)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId);
            return client;
        }

        public async Task<FormicaeUser> CreateUserAsync(string userName)
        {
            using var scope = Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<FormicaeUser>>();
            var user = new FormicaeUser
            {
                UserName = userName,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            var result = await users.CreateAsync(user);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
            return user;
        }

        public async Task<FormicaeUser> CreateViewerAsync(string userName)
            => await CreateUserWithRoleAsync(userName, (users, user) => users.GrantViewerAsync(user, CancellationToken.None));

        public async Task<FormicaeUser> CreateOperatorAsync(string userName)
            => await CreateUserWithRoleAsync(userName, (users, user) => users.GrantOperatorAsync(user, CancellationToken.None));

        public async Task<FormicaeUser> CreateAdminAsync(string userName)
            => await CreateUserWithRoleAsync(userName, (users, user) => users.GrantAdminAsync(user, CancellationToken.None));

        private async Task<FormicaeUser> CreateUserWithRoleAsync(
            string userName,
            Func<ManagementUserService, FormicaeUser, Task> grant)
        {
            using var scope = Services.CreateScope();
            var identityUsers = scope.ServiceProvider.GetRequiredService<UserManager<FormicaeUser>>();
            var user = new FormicaeUser
            {
                UserName = userName,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            var result = await identityUsers.CreateAsync(user);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));

            var users = scope.ServiceProvider.GetRequiredService<ManagementUserService>();
            await grant(users, user);
            return user;
        }

        public async Task<DevOpsIntegration> CreateGitHubIntegrationAsync(bool identityProviderEnabled = false)
        {
            using var scope = Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDevOpsIntegrationStore>();
            return await store.CreateAsync(new DevOpsIntegration
            {
                ProviderType = DevOpsProviderType.GitHub,
                DisplayName = "GitHub",
                GitHubAppClientId = "client-id",
                GitHubAppClientSecretReference = "client-secret",
                WebhookSecret = "webhook-secret",
                WebhookUrl = "https://formicae.example/api/webhooks/github",
                IdentityProviderEnabled = identityProviderEnabled,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }, CancellationToken.None);
        }

        public async Task<DevOpsIntegration?> GetIntegrationAsync(Guid integrationId)
        {
            using var scope = Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDevOpsIntegrationStore>();
            return await store.GetAsync(integrationId, CancellationToken.None);
        }

        public async Task<FailedWorkflow> CreateFailedWorkflowAsync()
        {
            using var scope = Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IWorkflowStore>();
            var workflow = new Workflow
            {
                IssueUrl = $"https://github.com/acme/widgets/issues/{Guid.NewGuid():N}",
                RepositoryUrl = "https://github.com/acme/widgets",
                Status = WorkflowStatus.Failed,
                CurrentStep = WorkflowStep.Plan,
                FailureReason = "Plan failed."
            };
            await store.CreateWorkflowAsync(workflow, CancellationToken.None);
            var run = await store.UpsertTaskRunAsync(new TaskRun
            {
                WorkflowId = workflow.Id,
                Kind = TaskRunKind.Plan,
                Status = TaskRunStatus.Failed,
                FailureReason = "Plan failed."
            }, CancellationToken.None);
            return new FailedWorkflow(workflow.Id, run.Id);
        }

        public async Task<bool> IsAdminAsync(FormicaeUser user)
        {
            using var scope = Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<FormicaeUser>>();
            return await users.IsInRoleAsync(user, ManagementUserService.ManagementAdminRole);
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";
        public const string UserIdHeader = "X-Test-UserId";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var userId = Request.Headers[UserIdHeader].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}



