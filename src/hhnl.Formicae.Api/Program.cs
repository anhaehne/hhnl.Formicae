using hhnl.Formicae.Api;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octokit;
using System.Security.Claims;
using System.Security.Cryptography;

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
    .AddEntityFrameworkStores<FormicaeDbContext>()
    .AddSignInManager();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultChallengeScheme = "GitHub";
    })
    .AddCookie(IdentityConstants.ApplicationScheme)
    .AddOAuth("GitHub", _ => { });
builder.Services.AddAuthorization();
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

app.MapHealthChecks("/healthz");

app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (IsAnonymousPath(context.Request.Path))
    {
        await next();
        return;
    }

    var store = context.RequestServices.GetRequiredService<IDevOpsIntegrationStore>();
    if (!await store.AnyIdentityProviderEnabledAsync(context.RequestAborted)
        || context.User.Identity?.IsAuthenticated == true)
    {
        await next();
        return;
    }

    if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    context.Response.Redirect(BuildGitHubChallengeUrl(context.Request));
});

app.MapGet("/api/auth/current-user", (ClaimsPrincipal user) =>
{
    if (user.Identity?.IsAuthenticated != true)
    {
        return Results.Ok(new { authenticated = false });
    }

    return Results.Ok(new
    {
        authenticated = true,
        name = user.Identity.Name,
        claims = user.Claims.Select(claim => new { claim.Type, claim.Value }).ToArray()
    });
});

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
    HttpContext context,
    DevOpsIntegrationService integrations,
    CancellationToken cancellationToken) =>
{
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
    var user = await client.User.Current();

    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Name, user.Login)
    };
    if (!string.IsNullOrWhiteSpace(user.Email))
    {
        claims.Add(new Claim(ClaimTypes.Email, user.Email));
    }

    var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme));
    var properties = new AuthenticationProperties { IsPersistent = false };
    properties.StoreTokens([
        new AuthenticationToken { Name = "access_token", Value = token.AccessToken }
    ]);
    await context.SignInAsync(IdentityConstants.ApplicationScheme, principal, properties);

    return Results.Redirect(parts[2]);
});

app.MapGet("/api/auth/external-callback", () => Results.Redirect("/"));

app.MapGet("/api/auth/github/repositories", async (
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
    {
        return Results.Unauthorized();
    }

    var accessToken = await context.GetTokenAsync("access_token");
    if (string.IsNullOrWhiteSpace(accessToken))
    {
        return Results.Unauthorized();
    }

    var client = new GitHubClient(new ProductHeaderValue("hhnl-formicae"))
    {
        Credentials = new Credentials(accessToken)
    };

    var repositories = await client.Repository.GetAllForCurrent(new RepositoryRequest
    {
        Affiliation = RepositoryAffiliation.Owner | RepositoryAffiliation.Collaborator | RepositoryAffiliation.OrganizationMember,
        Sort = RepositorySort.FullName,
        Direction = SortDirection.Ascending
    });

    return Results.Ok(repositories
        .Select(repository => new GitHubUserRepositoryResponse(
            repository.Owner.Login,
            repository.Name,
            repository.HtmlUrl,
            repository.DefaultBranch,
            repository.Private))
        .ToArray());
});

app.MapGet("/api/workflows", async (
    int? limit,
    WorkflowService workflowService,
    CancellationToken cancellationToken) =>
{
    var clampedLimit = Math.Clamp(limit ?? 25, 1, 100);
    return Results.Ok(await workflowService.ListRecentWorkflowsAsync(clampedLimit, cancellationToken));
});

app.MapPost("/api/worker/agent-messages", async (
    WorkerAgentMessageRequest request,
    WorkerAgentMessageService workerMessages,
    WorkflowTickNotifier notifier,
    CancellationToken cancellationToken) =>
{
    var accepted = await workerMessages.RecordAsync(request, cancellationToken);
    if (!accepted)
    {
        return Results.NotFound();
    }

    notifier.Signal();
    return Results.Accepted();
});

app.MapPost("/api/webhooks/github", async (
    HttpRequest request,
    GitHubWebhookHandler handler,
    CancellationToken cancellationToken) => await handler.HandleAsync(request, cancellationToken));

app.MapGet("/api/ai-settings", async (
    AiSettingsService aiSettingsService,
    CancellationToken cancellationToken) => Results.Ok(await aiSettingsService.GetAsync(cancellationToken)));

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
});

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
});

app.MapGet("/api/integrations/{integrationId:guid}", async (
    Guid integrationId,
    HttpRequest httpRequest,
    DevOpsIntegrationService integrations,
    CancellationToken cancellationToken) =>
{
    var integration = await integrations.GetAsync(integrationId, GetRequestBaseUri(httpRequest), cancellationToken);
    return integration is null ? Results.NotFound() : Results.Ok(integration);
});

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
});

app.MapPost("/api/integrations/{integrationId:guid}/webhook-secret", async (
    Guid integrationId,
    HttpRequest httpRequest,
    DevOpsIntegrationService integrations,
    CancellationToken cancellationToken) =>
{
    var integration = await integrations.RotateWebhookSecretAsync(integrationId, GetRequestBaseUri(httpRequest), cancellationToken);
    return integration is null ? Results.NotFound() : Results.Ok(integration);
});

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
});

app.MapGet("/api/integrations/{integrationId:guid}/repositories", async (
    Guid integrationId,
    DevOpsIntegrationService integrations,
    CancellationToken cancellationToken) =>
{
    var repositories = await integrations.ListRepositoriesAsync(integrationId, cancellationToken);
    return repositories is null ? Results.NotFound() : Results.Ok(repositories);
});

app.MapPut("/api/integrations/{integrationId:guid}/identity-provider", async (
    Guid integrationId,
    UpdateIdentityProviderRequest request,
    HttpRequest httpRequest,
    DevOpsIntegrationService integrations,
    CancellationToken cancellationToken) =>
{
    var integration = await integrations.SetIdentityProviderEnabledAsync(integrationId, request.Enabled, GetRequestBaseUri(httpRequest), cancellationToken);
    return integration is null ? Results.NotFound() : Results.Ok(integration);
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
});

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
});

app.MapGet("/api/workflows/{workflowId:guid}", async (
    Guid workflowId,
    WorkflowService workflowService,
    CancellationToken cancellationToken) =>
{
    var workflow = await workflowService.GetWorkflowAsync(workflowId, cancellationToken);
    return workflow is null ? Results.NotFound() : Results.Ok(workflow);
});

app.MapGet("/api/workflows/{workflowId:guid}/runs", async (
    Guid workflowId,
    WorkflowService workflowService,
    CancellationToken cancellationToken) => Results.Ok(await workflowService.ListRunsAsync(workflowId, cancellationToken)));

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
});

app.MapGet("/api/workflows/{workflowId:guid}/events", async (
    Guid workflowId,
    WorkflowService workflowService,
    CancellationToken cancellationToken) => Results.Ok(await workflowService.ListEventsAsync(workflowId, cancellationToken)));

app.MapGet("/api/workflows/{workflowId:guid}/signals", async (
    Guid workflowId,
    WorkflowObservabilityService observabilityService,
    CancellationToken cancellationToken) => Results.Ok(await observabilityService.GetWorkflowSignalsAsync(workflowId, cancellationToken)));

app.MapGet("/api/workflows/{workflowId:guid}/chat-messages", async (
    Guid workflowId,
    WorkflowService workflowService,
    CancellationToken cancellationToken) => Results.Ok(await workflowService.ListChatMessagesAsync(workflowId, cancellationToken)));

app.MapGet("/api/workflows/{workflowId:guid}/logs", async (
    Guid workflowId,
    WorkflowService workflowService,
    CancellationToken cancellationToken) => Results.Ok(await workflowService.ListLogsAsync(workflowId, cancellationToken)));

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

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

static bool IsAnonymousPath(PathString path)
    => IsIdentityProviderRestartPath(path)
        || path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/auth", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/webhooks", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase)
        || path.Value is "/favicon.ico" or "/index.html";

static bool IsIdentityProviderRestartPath(PathString path)
    => path.Value?.Contains("/identity-provider/restart", StringComparison.OrdinalIgnoreCase) == true
        && path.StartsWithSegments("/api/integrations", StringComparison.OrdinalIgnoreCase);
