using hhnl.Formicae.Api;
using hhnl.Formicae.Application.Management;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.Identity;
using hhnl.Formicae.Infrastructure.Kubernetes;
using hhnl.Formicae.Infrastructure.OpenHands;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.WebUtilities;
using Octokit;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Reflection;

const string GitHubOAuthStateCookieName = ".Formicae.GitHubOAuthState";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.Configure<GitHubWebhookOptions>(builder.Configuration.GetSection("GitHubWebhooks"));
builder.Services.AddSingleton<WorkflowTickNotifier>();
builder.Services.AddSingleton<IWorkflowTickSignal>(serviceProvider => serviceProvider.GetRequiredService<WorkflowTickNotifier>());
builder.Services.AddScoped<GitHubWebhookHandler>();
builder.Services.AddFormicaeInfrastructure(builder.Configuration);
builder.Services
    .AddIdentityCore<FormicaeUser>()
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<FormicaeDbContext>()
    .AddSignInManager();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
    })
    .AddCookie(IdentityConstants.ApplicationScheme, options =>
    {
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(BuildGitHubChallengeUrl(context.Request));
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            context.Response.Redirect("/?auth=unauthorized");
            return Task.CompletedTask;
        };
    })
    .AddOAuth("GitHub", _ => { });
builder.Services.AddAuthorization(options =>
{
    // Coarse management permissions intentionally map to future workflow capabilities
    // such as trigger, retry, approval, secret access, and admin.
    options.AddPolicy(ManagementAuthorization.WorkflowView, policy =>
        policy.Requirements.Add(new ManagementPermissionRequirement(ManagementAuthorization.WorkflowView)));
    options.AddPolicy(ManagementAuthorization.WorkflowOperate, policy =>
        policy.Requirements.Add(new ManagementPermissionRequirement(ManagementAuthorization.WorkflowOperate)));
    options.AddPolicy(ManagementAuthorization.ManagementAdmin, policy =>
        policy.Requirements.Add(new ManagementPermissionRequirement(ManagementAuthorization.ManagementAdmin)));
});
builder.Services.AddScoped<IAuthorizationHandler, ManagementAuthorizedHandler>();
builder.Services.AddSingleton<IConfigureOptions<OAuthOptions>, GitHubOAuthOptionsConfiguration>();
builder.Services.AddHostedService<WorkflowBackgroundService>();

var app = builder.Build();

var usesPostgresPersistence = !app.Configuration.GetValue("UseFakeAdapters", true)
    && !string.Equals(app.Configuration["PersistenceMode"], "InMemory", StringComparison.OrdinalIgnoreCase);

if (usesPostgresPersistence)
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<FormicaeDbContext>();
    await dbContext.Database.MigrateAsync();
}

await using (var scope = app.Services.CreateAsyncScope())
{
    var workflowDefinitions = scope.ServiceProvider.GetRequiredService<WorkflowDefinitionService>();
    await workflowDefinitions.EnsureDefaultWorkflowDefinitionAsync(CancellationToken.None);
}

app.MapHealthChecks("/healthz");
app.MapGet("/api/version", () => Results.Ok(new
{
    version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString()
        ?? "unknown"
}));

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/auth/current-user", async (
    ClaimsPrincipal principal,
    ManagementUserService users,
    UserManager<FormicaeUser> userManager,
    IDevOpsIntegrationStore integrations,
    IOptions<ManagementAuthOptions> authOptions,
    IHostEnvironment environment,
    CancellationToken cancellationToken) =>
{
    var authRequired = await integrations.AnyIdentityProviderEnabledAsync(cancellationToken);
    var bypassed = !authOptions.Value.Enabled
        || (authOptions.Value.BypassForLocalDevelopment && environment.IsDevelopment());
    if (principal.Identity?.IsAuthenticated != true)
    {
        return Results.Ok(new
        {
            authenticated = false,
            authorized = bypassed,
            authRequired,
            canViewWorkflows = bypassed,
            canTriggerWorkflows = bypassed,
            canAdminister = bypassed
        });
    }

    var user = await users.GetCurrentUserAsync(principal);
    var logins = user is null ? Array.Empty<UserLoginInfo>() : await userManager.GetLoginsAsync(user);
    var canViewWorkflows = bypassed || await users.IsInPermissionAsync(principal, ManagementAuthorization.WorkflowView);
    var canTriggerWorkflows = bypassed || await users.IsInPermissionAsync(principal, ManagementAuthorization.WorkflowOperate);
    var canAdminister = bypassed || await users.IsInPermissionAsync(principal, ManagementAuthorization.ManagementAdmin);
    return Results.Ok(new
    {
        authenticated = true,
        id = user?.Id,
        authorized = canViewWorkflows || canTriggerWorkflows || canAdminister,
        authRequired,
        canViewWorkflows,
        canTriggerWorkflows,
        canAdminister,
        name = user?.DisplayName ?? principal.Identity.Name,
        email = user?.Email,
        provider = logins.FirstOrDefault()?.LoginProvider
    });
});
app.MapGet("/api/auth/roles", () => Results.Ok(ManagementUserService.RoleDefinitions))
    .RequireAuthorization(ManagementAuthorization.ManagementAdmin);

app.MapGet("/api/auth/users", async (
    ManagementUserService users,
    CancellationToken cancellationToken) => Results.Ok(await users.ListUsersAsync(cancellationToken)))
    .RequireAuthorization(ManagementAuthorization.ManagementAdmin);

app.MapPut("/api/auth/users/{userId}/roles", async (
    string userId,
    UpdateManagementUserRolesRequest request,
    ClaimsPrincipal principal,
    ManagementUserService users,
    CancellationToken cancellationToken) =>
{
    var requestedRoles = request.Roles ?? [];
    var currentUser = await users.GetCurrentUserAsync(principal);
    if (currentUser?.Id == userId && !requestedRoles.Contains(ManagementUserService.ManagementAdminRole, StringComparer.Ordinal))
    {
        return Results.BadRequest(new { error = "You cannot remove your own management admin role." });
    }

    try
    {
        var updated = await users.UpdateRolesAsync(userId, requestedRoles, cancellationToken);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
})
    .RequireAuthorization(ManagementAuthorization.ManagementAdmin);
app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(IdentityConstants.ApplicationScheme);
    return Results.NoContent();
});

app.MapGet("/api/auth/external-challenge", (HttpContext context)
    => Results.Redirect(BuildGitHubChallengeUrl(context.Request)));

app.MapGet("/api/auth/github/challenge", async (
    Guid? integrationId,
    HttpContext context,
    DevOpsIntegrationService integrations,
    CancellationToken cancellationToken) =>
{
    var integration = integrationId.HasValue
        ? await integrations.GetRawAsync(integrationId.Value, cancellationToken)
        : await integrations.GetDefaultGitHubIntegrationAsync(cancellationToken);
    if (integration is null)
    {
        return Results.NotFound();
    }

    var returnUrl = context.Request.Query["returnUrl"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(returnUrl) || returnUrl[0] != '/')
    {
        returnUrl = "/";
    }

    var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    context.Response.Cookies.Append(
        GitHubOAuthStateCookieName,
        $"{state}|{integration.Id}|{returnUrl}",
        new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = true,
            Expires = DateTimeOffset.UtcNow.AddMinutes(15)
        });

    var client = new GitHubClient(new ProductHeaderValue("hhnl-formicae"));
    var request = new OauthLoginRequest(integration.GitHubAppClientId)
    {
        State = state,
        RedirectUri = new Uri(GetPublicBaseUri(context.Request), "/api/auth/github/callback")
    };
    request.Scopes.Add("read:user");
    request.Scopes.Add("user:email");
    request.Scopes.Add("repo");

    return Results.Redirect(client.Oauth.GetGitHubLoginUrl(request).ToString());
});

app.MapGet("/api/auth/github/callback", async (
    string? code,
    string? state,
    long? installation_id,
    string? setup_action,
    HttpContext context,
    DevOpsIntegrationService integrations,
    ManagementUserService managementUsers,
    InviteService invites,
    SignInManager<FormicaeUser> signInManager,
    CancellationToken cancellationToken) =>
{
    if (installation_id.HasValue || !string.IsNullOrWhiteSpace(setup_action))
    {
        return await RedirectToGitHubInstallationResultAsync(state, installation_id, setup_action, integrations, cancellationToken);
    }

    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
    {
        return Results.Redirect("/?auth=failed");
    }

    var stateCookie = context.Request.Cookies[GitHubOAuthStateCookieName];
    context.Response.Cookies.Delete(GitHubOAuthStateCookieName);
    var parts = stateCookie?.Split('|', 3);
    if (parts is not { Length: 3 }
        || !string.Equals(parts[0], state, StringComparison.Ordinal)
        || !Guid.TryParse(parts[1], out var integrationId))
    {
        return Results.BadRequest(new { error = "Invalid GitHub OAuth state." });
    }

    var integration = await integrations.GetRawAsync(integrationId, cancellationToken);
    if (integration is null || string.IsNullOrWhiteSpace(integration.GitHubAppClientSecretReference))
    {
        return Results.BadRequest(new { error = "GitHub integration is not configured for OAuth." });
    }

    var client = new GitHubClient(new ProductHeaderValue("hhnl-formicae"));
    var token = await client.Oauth.CreateAccessToken(new OauthTokenRequest(
        integration.GitHubAppClientId,
        integration.GitHubAppClientSecretReference,
        code));
    client.Credentials = new Credentials(token.AccessToken);
    integration.GitHubOAuthAccessToken = token.AccessToken;
    integration.UpdatedAt = DateTimeOffset.UtcNow;
    await context.RequestServices.GetRequiredService<IDevOpsIntegrationStore>().UpdateAsync(integration, cancellationToken);

    var gitHubUser = await client.User.Current();
    var displayName = string.IsNullOrWhiteSpace(gitHubUser.Name) ? gitHubUser.Login : gitHubUser.Name;
    var user = await managementUsers.FindOrCreateExternalUserAsync(new ExternalUserProfile(
        "GitHub",
        gitHubUser.Id.ToString(),
        "GitHub",
        gitHubUser.Login,
        displayName,
        gitHubUser.Email), cancellationToken);

    var properties = new AuthenticationProperties { IsPersistent = false };
    properties.StoreTokens([
        new AuthenticationToken { Name = "access_token", Value = token.AccessToken }
    ]);
    await signInManager.SignInAsync(user, properties);

    return Results.Redirect(await ApplyInviteFromReturnUrlAsync(parts[2], user, invites, cancellationToken));
});

app.MapGet("/api/auth/external-callback", () => Results.Redirect("/"));

app.MapGet("/api/auth/github/installations/callback", async (
    string? state,
    long? installation_id,
    string? setup_action,
    DevOpsIntegrationService integrations,
    CancellationToken cancellationToken) =>
{
    return await RedirectToGitHubInstallationResultAsync(state, installation_id, setup_action, integrations, cancellationToken);
});

app.MapGet("/api/auth/github/repositories", async (
    Guid? integrationId,
    DevOpsIntegrationService integrations,
    IGitHubAppClient gitHubAppClient,
    CancellationToken cancellationToken) =>
{
    var integration = integrationId.HasValue
        ? await integrations.GetRawAsync(integrationId.Value, cancellationToken)
        : await integrations.GetDefaultGitHubIntegrationAsync(cancellationToken);
    if (integration is null)
    {
        return Results.NotFound();
    }

    try
    {
        var repositories = await gitHubAppClient.ListInstallationRepositoriesAsync(integration, cancellationToken);
        return Results.Ok(repositories.Select(repository => new GitHubUserRepositoryResponse(
            repository.Owner,
            repository.Name,
            repository.RepositoryUrl,
            repository.DefaultBranch,
            repository.Private,
            repository.InstallationId,
            repository.InstallationAccount)).ToArray());
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Octokit.ApiException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(ManagementAuthorization.ManagementAdmin);

app.MapPost("/api/auth/invites", async (
    ClaimsPrincipal user,
    InviteService invites,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Created("/api/auth/invites", await invites.CreateInviteAsync(user, cancellationToken));
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }
}).RequireAuthorization(ManagementAuthorization.ManagementAdmin);

app.MapGet("/api/auth/invites", async (
    ClaimsPrincipal user,
    InviteService invites,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await invites.ListInvitesAsync(user, cancellationToken));
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }
}).RequireAuthorization(ManagementAuthorization.ManagementAdmin);

app.MapPost("/api/auth/invites/redeem", async (
    RedeemInviteRequest request,
    ClaimsPrincipal user,
    InviteService invites,
    CancellationToken cancellationToken) =>
{
    try
    {
        await invites.RedeemInviteAsync(user, request.Code, cancellationToken);
        return Results.NoContent();
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization();

app.MapGet("/api/workflows", async (
    int? limit,
    WorkflowService workflowService,
    CancellationToken cancellationToken) =>
{
    var clampedLimit = Math.Clamp(limit ?? 25, 1, 100);
    return Results.Ok(await workflowService.ListRecentWorkflowsAsync(clampedLimit, cancellationToken));
}).RequireAuthorization(ManagementAuthorization.WorkflowView);

app.MapPost("/api/worker/agent-messages", async (
    WorkerAgentMessageRequest request,
    HttpRequest httpRequest,
    WorkerAgentMessageService workerMessages,
    WorkflowTickNotifier notifier,
    IOptions<KubernetesJobOptions> kubernetesJobOptions,
    CancellationToken cancellationToken) =>
{
    var callbackSecret = kubernetesJobOptions.Value.WorkerCallbackSecret;
    if (!string.IsNullOrWhiteSpace(callbackSecret)
        && !string.Equals(httpRequest.Headers["X-Formicae-Worker-Callback-Secret"].FirstOrDefault(), callbackSecret, StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }

    var accepted = await workerMessages.RecordAsync(request, cancellationToken);
    if (!accepted)
    {
        return Results.NotFound();
    }

    notifier.Signal();
    return Results.Accepted();
});

app.MapPost("/api/worker/agent-auth", async (
    WorkerAgentAuthRefreshRequest request,
    HttpRequest httpRequest,
    WorkerAgentAuthRefreshService authRefreshes,
    IOptions<KubernetesJobOptions> kubernetesJobOptions,
    CancellationToken cancellationToken) =>
{
    var callbackSecret = kubernetesJobOptions.Value.WorkerCallbackSecret;
    if (!string.IsNullOrWhiteSpace(callbackSecret)
        && !string.Equals(httpRequest.Headers["X-Formicae-Worker-Callback-Secret"].FirstOrDefault(), callbackSecret, StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }

    var accepted = await authRefreshes.RecordAsync(request, cancellationToken);
    return accepted ? Results.Accepted() : Results.NotFound();
});
app.MapPost("/api/webhooks/github", async (
    HttpRequest request,
    GitHubWebhookHandler handler,
    CancellationToken cancellationToken) => await handler.HandleAsync(request, cancellationToken));

app.MapGet("/api/ai-settings", async (
    AiSettingsService aiSettingsService,
    CancellationToken cancellationToken) => Results.Ok(await aiSettingsService.ListAsync(cancellationToken)));

app.MapPost("/api/ai-settings/{settingsId}/codex-auth/connect", async (
    string settingsId,
    [FromServices] CodexAuthSetupService codexAuthSetup,
    CancellationToken cancellationToken) =>
{
    try
    {
        var started = await codexAuthSetup.StartAsync(settingsId, cancellationToken);
        return Results.Accepted($"/api/ai-settings/{settingsId}/codex-auth/connect/{started.JobName}", started);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(ManagementAuthorization.ManagementAdmin);

app.MapGet("/api/ai-settings/{settingsId}/codex-auth/connect/{jobName}", async (
    string settingsId,
    string jobName,
    [FromServices] CodexAuthSetupService codexAuthSetup,
    CancellationToken cancellationToken) => Results.Ok(await codexAuthSetup.GetStatusAsync(settingsId, jobName, cancellationToken)))
    .RequireAuthorization(ManagementAuthorization.ManagementAdmin);

app.MapPut("/api/ai-settings", async (
    UpdateAiSettingsRequest request,
    AiSettingsService aiSettingsService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await aiSettingsService.UpdateAsync(request, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(ManagementAuthorization.ManagementAdmin);

app.MapGet("/api/integrations", async (
    DevOpsIntegrationService integrations,
    CancellationToken cancellationToken) => Results.Ok(await integrations.ListAsync(cancellationToken)));

app.MapPost("/api/integrations/github", async (
    CreateGitHubIntegrationRequest request,
    HttpRequest httpRequest,
    DevOpsIntegrationService integrations,
    CancellationToken cancellationToken) =>
{
    try
    {
        var integration = await integrations.CreateGitHubIntegrationAsync(request, GetRequestBaseUri(httpRequest), cancellationToken);
        return Results.Created($"/api/integrations/{integration.Id}", integration);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Octokit.ApiException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(ManagementAuthorization.ManagementAdmin);

app.MapGet("/api/integrations/{integrationId:guid}", async (
    Guid integrationId,
    HttpRequest httpRequest,
    DevOpsIntegrationService integrations,
    CancellationToken cancellationToken) =>
{
    var integration = await integrations.GetAsync(integrationId, GetRequestBaseUri(httpRequest), cancellationToken);
    return integration is null ? Results.NotFound() : Results.Ok(integration);
}).RequireAuthorization(ManagementAuthorization.ManagementAdmin);

app.MapDelete("/api/integrations/{integrationId:guid}", async (
    Guid integrationId,
    DevOpsIntegrationService integrations,
    CancellationToken cancellationToken) =>
{
    return await integrations.DeleteAsync(integrationId, cancellationToken)
        ? Results.NoContent()
        : Results.NotFound();
}).RequireAuthorization(ManagementAuthorization.ManagementAdmin);

app.MapPut("/api/integrations/{integrationId:guid}/github-app", async (
    Guid integrationId,
    UpdateGitHubAppRequest request,
    HttpRequest httpRequest,
    DevOpsIntegrationService integrations,
    CancellationToken cancellationToken) =>
{
    try
    {
        var integration = await integrations.UpdateGitHubAppAsync(integrationId, request, GetRequestBaseUri(httpRequest), cancellationToken);
        return integration is null ? Results.NotFound() : Results.Ok(integration);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Octokit.ApiException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(ManagementAuthorization.ManagementAdmin);
app.MapPost("/api/integrations/{integrationId:guid}/webhook-secret", async (
    Guid integrationId,
    HttpRequest httpRequest,
    DevOpsIntegrationService integrations,
    CancellationToken cancellationToken) =>
{
    var integration = await integrations.RotateWebhookSecretAsync(integrationId, GetRequestBaseUri(httpRequest), cancellationToken);
    return integration is null ? Results.NotFound() : Results.Ok(integration);
}).RequireAuthorization(ManagementAuthorization.ManagementAdmin);

app.MapPost("/api/integrations/{integrationId:guid}/repositories", async (
    Guid integrationId,
    AddConnectedRepositoryRequest request,
    DevOpsIntegrationService integrations,
    CancellationToken cancellationToken) =>
{
    try
    {
        var repository = await integrations.AddRepositoryAsync(integrationId, request, cancellationToken);
        return repository is null ? Results.NotFound() : Results.Created($"/api/integrations/{integrationId}/repositories/{repository.Id}", repository);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(ManagementAuthorization.ManagementAdmin);

app.MapGet("/api/integrations/{integrationId:guid}/repositories", async (
    Guid integrationId,
    DevOpsIntegrationService integrations,
    CancellationToken cancellationToken) =>
{
    var repositories = await integrations.ListRepositoriesAsync(integrationId, cancellationToken);
    return repositories is null ? Results.NotFound() : Results.Ok(repositories);
}).RequireAuthorization(ManagementAuthorization.ManagementAdmin);

app.MapDelete("/api/integrations/{integrationId:guid}/repositories/{repositoryId:guid}", async (
    Guid integrationId,
    Guid repositoryId,
    DevOpsIntegrationService integrations,
    CancellationToken cancellationToken) =>
{
    var removed = await integrations.DeleteRepositoryAsync(integrationId, repositoryId, cancellationToken);
    return removed switch
    {
        null => Results.NotFound(),
        true => Results.NoContent(),
        false => Results.NotFound()
    };
}).RequireAuthorization(ManagementAuthorization.ManagementAdmin);

app.MapGet("/api/workflow-definitions", async (
    WorkflowDefinitionService workflowDefinitions,
    CancellationToken cancellationToken) => Results.Ok(await workflowDefinitions.ListAsync(cancellationToken)))
    .RequireAuthorization(ManagementAuthorization.WorkflowView);

app.MapGet("/api/workflow-definitions/{definitionId:guid}", async (
    Guid definitionId,
    WorkflowDefinitionService workflowDefinitions,
    CancellationToken cancellationToken) =>
{
    var definition = await workflowDefinitions.GetAsync(definitionId, cancellationToken);
    return definition is null ? Results.NotFound() : Results.Ok(definition);
}).RequireAuthorization(ManagementAuthorization.WorkflowView);

app.MapPost("/api/workflow-definitions", async (
    CreateWorkflowDefinitionRequest request,
    WorkflowDefinitionService workflowDefinitions,
    CancellationToken cancellationToken) =>
{
    try
    {
        var definition = await workflowDefinitions.CreateAsync(request, cancellationToken);
        return Results.Created($"/api/workflow-definitions/{definition.Id}", definition);
    }
    catch (WorkflowDefinitionValidationException exception)
    {
        return Results.BadRequest(new { errors = exception.Errors });
    }
}).RequireAuthorization(ManagementAuthorization.ManagementAdmin);

app.MapPost("/api/workflow-definitions/{definitionId:guid}/versions", async (
    Guid definitionId,
    CreateWorkflowDefinitionVersionRequest request,
    WorkflowDefinitionService workflowDefinitions,
    CancellationToken cancellationToken) =>
{
    try
    {
        var version = await workflowDefinitions.CreateVersionAsync(definitionId, request, cancellationToken);
        return Results.Created($"/api/workflow-definitions/{definitionId}", version);
    }
    catch (WorkflowDefinitionNotFoundException)
    {
        return Results.NotFound();
    }
    catch (WorkflowDefinitionValidationException exception)
    {
        return Results.BadRequest(new { errors = exception.Errors });
    }
}).RequireAuthorization(ManagementAuthorization.ManagementAdmin);

app.MapPut("/api/integrations/{integrationId:guid}/identity-provider", async (
    Guid integrationId,
    UpdateIdentityProviderRequest request,
    HttpRequest httpRequest,
    ClaimsPrincipal user,
    DevOpsIntegrationService integrations,
    IDevOpsIntegrationStore integrationStore,
    ManagementUserService managementUsers,
    IAuthorizationService authorization,
    IOptions<ManagementAuthOptions> authOptions,
    IHostEnvironment environment,
    CancellationToken cancellationToken) =>
{
    try
    {
        if (request.Enabled)
        {
            if (await integrationStore.AnyIdentityProviderEnabledAsync(cancellationToken)
                && authOptions.Value.Enabled
                && !(authOptions.Value.BypassForLocalDevelopment && environment.IsDevelopment()))
            {
                var authResult = await authorization.AuthorizeAsync(user, ManagementAuthorization.ManagementAdmin);
                if (!authResult.Succeeded)
                {
                    return user.Identity?.IsAuthenticated == true ? Results.Forbid() : Results.Unauthorized();
                }
            }

            var currentUser = await managementUsers.GetCurrentUserAsync(user);
            if (currentUser is null)
            {
                return Results.Unauthorized();
            }

            await managementUsers.GrantAdminAsync(currentUser, cancellationToken);
        }
        else if (authOptions.Value.Enabled
            && !(authOptions.Value.BypassForLocalDevelopment && environment.IsDevelopment()))
        {
            var authResult = await authorization.AuthorizeAsync(user, ManagementAuthorization.ManagementAdmin);
            if (!authResult.Succeeded)
            {
                return user.Identity?.IsAuthenticated == true ? Results.Forbid() : Results.Unauthorized();
            }
        }

        var integration = await integrations.SetIdentityProviderEnabledAsync(integrationId, request.Enabled, GetRequestBaseUri(httpRequest), cancellationToken);
        return integration is null ? Results.NotFound() : Results.Ok(integration);
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapPost("/api/integrations/{integrationId:guid}/identity-provider/restart", async (
    Guid integrationId,
    HttpRequest httpRequest,
    DevOpsIntegrationService integrations,
    IHostApplicationLifetime applicationLifetime,
    CancellationToken cancellationToken) =>
{
    var current = await integrations.GetAsync(integrationId, GetRequestBaseUri(httpRequest), cancellationToken);
    if (current is null)
    {
        return Results.NotFound();
    }

    if (!current.IdentityProviderEnabled)
    {
        return Results.BadRequest(new { error = "Enable the identity provider before restarting." });
    }

    var integration = await integrations.MarkIdentityProviderRestartedAsync(integrationId, GetRequestBaseUri(httpRequest), cancellationToken);
    _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        applicationLifetime.StopApplication();
    }, CancellationToken.None);

    return integration is null ? Results.NotFound() : Results.Ok(integration);
}).RequireAuthorization(ManagementAuthorization.ManagementAdmin);

app.MapPost("/api/workflows/github-issue", async (
    StartGitHubIssueWorkflowRequest request,
    WorkflowService workflowService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var workflow = await workflowService.StartGitHubIssueWorkflowAsync(request, cancellationToken);
        return Results.Accepted($"/api/workflows/{workflow.WorkflowId}", workflow);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (WorkflowDefinitionNotFoundException)
    {
        return Results.NotFound();
    }
    catch (WorkflowDefinitionValidationException exception)
    {
        return Results.BadRequest(new { errors = exception.Errors });
    }
}).RequireAuthorization(ManagementAuthorization.WorkflowOperate);

app.MapGet("/api/workflows/{workflowId:guid}", async (
    Guid workflowId,
    WorkflowService workflowService,
    CancellationToken cancellationToken) =>
{
    var workflow = await workflowService.GetWorkflowAsync(workflowId, cancellationToken);
    return workflow is null ? Results.NotFound() : Results.Ok(workflow);
}).RequireAuthorization(ManagementAuthorization.WorkflowView);

app.MapGet("/api/workflows/{workflowId:guid}/runs", async (
    Guid workflowId,
    WorkflowService workflowService,
    CancellationToken cancellationToken) => Results.Ok(await workflowService.ListRunsAsync(workflowId, cancellationToken)))
    .RequireAuthorization(ManagementAuthorization.WorkflowView);

app.MapPost("/api/workflows/{workflowId:guid}/runs/{taskRunId:guid}/retry", async (
    Guid workflowId,
    Guid taskRunId,
    WorkflowService workflowService,
    WorkflowTickNotifier notifier,
    CancellationToken cancellationToken) =>
{
    try
    {
        var workflow = await workflowService.RetryTaskRunAsync(workflowId, taskRunId, cancellationToken);
        if (workflow is null)
        {
            return Results.NotFound();
        }

        notifier.Signal();
        return Results.Ok(workflow);
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(ManagementAuthorization.WorkflowOperate);

app.MapPost("/api/workflows/{workflowId:guid}/retry", async (
    Guid workflowId,
    WorkflowService workflowService,
    WorkflowTickNotifier notifier,
    CancellationToken cancellationToken) =>
{
    try
    {
        var workflow = await workflowService.RetryWorkflowAsync(workflowId, cancellationToken);
        if (workflow is null)
        {
            return Results.NotFound();
        }

        notifier.Signal();
        return Results.Ok(workflow);
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(ManagementAuthorization.WorkflowOperate);

app.MapGet("/api/workflows/{workflowId:guid}/events", async (
    Guid workflowId,
    WorkflowService workflowService,
    CancellationToken cancellationToken) => Results.Ok(await workflowService.ListEventsAsync(workflowId, cancellationToken)))
    .RequireAuthorization(ManagementAuthorization.WorkflowView);

app.MapGet("/api/workflows/{workflowId:guid}/signals", async (
    Guid workflowId,
    WorkflowObservabilityService observabilityService,
    CancellationToken cancellationToken) => Results.Ok(await observabilityService.GetWorkflowSignalsAsync(workflowId, cancellationToken)))
    .RequireAuthorization(ManagementAuthorization.WorkflowView);

app.MapGet("/api/workflows/{workflowId:guid}/chat-messages", async (
    Guid workflowId,
    WorkflowService workflowService,
    CancellationToken cancellationToken) => Results.Ok(await workflowService.ListChatMessagesAsync(workflowId, cancellationToken)))
    .RequireAuthorization(ManagementAuthorization.WorkflowView);

app.MapGet("/api/workflows/{workflowId:guid}/logs", async (
    Guid workflowId,
    WorkflowService workflowService,
    CancellationToken cancellationToken) => Results.Ok(await workflowService.ListLogsAsync(workflowId, cancellationToken)))
    .RequireAuthorization(ManagementAuthorization.WorkflowView);

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

static async Task<string> ApplyInviteFromReturnUrlAsync(
    string returnUrl,
    FormicaeUser user,
    InviteService invites,
    CancellationToken cancellationToken)
{
    var inviteCode = GetReturnUrlQueryValue(returnUrl, "invite");
    if (string.IsNullOrWhiteSpace(inviteCode))
    {
        return returnUrl;
    }

    try
    {
        await invites.RedeemInviteAsync(user, inviteCode, cancellationToken);
        return BuildReturnUrlWithoutInvite(returnUrl, "inviteRedeemed", "true");
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
    {
        return BuildReturnUrlWithoutInvite(returnUrl, "inviteError", exception.Message);
    }
}

static string? GetReturnUrlQueryValue(string returnUrl, string key)
{
    var queryStart = returnUrl.IndexOf('?', StringComparison.Ordinal);
    if (queryStart < 0)
    {
        return null;
    }

    var query = returnUrl[queryStart..];
    return QueryHelpers.ParseQuery(query).TryGetValue(key, out var value) ? value.FirstOrDefault() : null;
}

static string BuildReturnUrlWithoutInvite(string returnUrl, string statusKey, string statusValue)
{
    if (string.IsNullOrWhiteSpace(returnUrl) || returnUrl[0] != '/')
    {
        return "/";
    }

    var queryStart = returnUrl.IndexOf('?', StringComparison.Ordinal);
    var path = queryStart < 0 ? returnUrl : returnUrl[..queryStart];
    var query = queryStart < 0 ? string.Empty : returnUrl[queryStart..];
    var values = QueryHelpers.ParseQuery(query)
        .Where(pair => !string.Equals(pair.Key, "invite", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(pair.Key, "inviteRedeemed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(pair.Key, "inviteError", StringComparison.OrdinalIgnoreCase))
        .SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key, value)))
        .Append(new KeyValuePair<string, string?>(statusKey, statusValue));

    return path + QueryString.Create(values);
}
static async Task<IResult> RedirectToGitHubInstallationResultAsync(
    string? state,
    long? installationId,
    string? setupAction,
    DevOpsIntegrationService integrations,
    CancellationToken cancellationToken)
{
    Guid? integrationId = Guid.TryParse(state, out var parsedIntegrationId) ? parsedIntegrationId : null;
    if (!integrationId.HasValue)
    {
        integrationId = (await integrations.GetDefaultGitHubIntegrationAsync(cancellationToken))?.Id;
    }

    var query = new QueryString()
        .Add("page", "repositories");
    if (integrationId.HasValue)
    {
        query = query.Add("integrationId", integrationId.Value.ToString());
    }

    if (installationId.HasValue)
    {
        query = query.Add("installationId", installationId.Value.ToString());
    }

    if (!string.IsNullOrWhiteSpace(setupAction))
    {
        query = query.Add("setupAction", setupAction);
    }

    return Results.Redirect($"/{query}");
}
static Uri GetRequestBaseUri(HttpRequest request)
    => new($"{request.Scheme}://{request.Host}");

static string BuildGitHubChallengeUrl(HttpRequest request)
{
    var returnUrl = request.Query["returnUrl"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(returnUrl) || returnUrl[0] != '/')
    {
        returnUrl = request.PathBase.Add(request.Path).Add(request.QueryString).ToString();
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            returnUrl = "/";
        }
    }

    return $"/api/auth/github/challenge?returnUrl={Uri.EscapeDataString(returnUrl)}";
}

static Uri GetPublicBaseUri(HttpRequest request)
{
    var scheme = request.Headers["X-Forwarded-Proto"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(scheme))
    {
        scheme = request.IsHttps ? Uri.UriSchemeHttps : Uri.UriSchemeHttps;
    }

    var host = request.Headers["X-Forwarded-Host"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(host))
    {
        host = request.Host.Value;
    }

    return new Uri($"{scheme}://{host}");
}

public sealed record UpdateManagementUserRolesRequest(IReadOnlyList<string>? Roles);

public partial class Program;
