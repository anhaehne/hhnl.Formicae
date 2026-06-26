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

    await context.ChallengeAsync();
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

app.MapGet("/api/auth/external-challenge", (HttpContext context) =>
{
    var redirectUri = context.Request.Query["returnUrl"].FirstOrDefault();
    redirectUri = string.IsNullOrWhiteSpace(redirectUri) ? "/" : redirectUri;
    return Results.Challenge(new AuthenticationProperties { RedirectUri = redirectUri }, ["GitHub"]);
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

static bool IsAnonymousPath(PathString path)
    => path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/auth", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/webhooks", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase)
        || path.Value is "/favicon.ico" or "/index.html";
