using hhnl.Formicae.Api;
using hhnl.Formicae.Application.Auth;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);
var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();

if (authOptions.Enabled)
{
    GitHubOAuthExtensions.ValidateFormicaeOAuthProvider(authOptions);

    builder.Services
        .AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = FormicaeAuth.CookieScheme;
            options.DefaultSignInScheme = FormicaeAuth.CookieScheme;
            options.DefaultChallengeScheme = FormicaeAuth.GitHubScheme;
        })
        .AddCookie(FormicaeAuth.CookieScheme, options =>
        {
            options.Cookie.Name = string.IsNullOrWhiteSpace(authOptions.CookieName) ? "formicae_auth" : authOptions.CookieName;
            options.Events = new CookieAuthenticationEvents
            {
                OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                },
                OnRedirectToAccessDenied = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                }
            };
        })
        .AddFormicaeOAuthProvider(authOptions);
}

builder.Services.AddHealthChecks();
builder.Services.Configure<GitHubWebhookOptions>(builder.Configuration.GetSection("GitHubWebhooks"));
builder.Services.AddSingleton<WorkflowTickNotifier>();
builder.Services.AddSingleton<IWorkflowTickSignal>(serviceProvider => serviceProvider.GetRequiredService<WorkflowTickNotifier>());
builder.Services.AddScoped<GitHubWebhookHandler>();
builder.Services.AddFormicaeInfrastructure(builder.Configuration);
builder.Services.AddScoped<IAuthorizationHandler, ManagementAuthHandler>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(FormicaeAuth.ManagementPolicy, policy => policy.Requirements.Add(new ManagementAuthRequirement()));
});
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

if (authOptions.Enabled)
{
    app.UseAuthentication();
}

app.UseAuthorization();

app.MapGet("/api/auth/session", async (
    ClaimsPrincipal user,
    AuthInviteService invites,
    IOptions<AuthOptions> options,
    CancellationToken cancellationToken) =>
{
    var identity = FormicaeAuth.GetGitHubIdentity(user);
    var authenticated = user.Identity?.IsAuthenticated == true && identity is not null;
    var allowed = !options.Value.Enabled || await invites.IsAllowedAsync(identity, cancellationToken);

    return Results.Ok(new AuthSessionResponse(
        options.Value.Enabled,
        authenticated,
        allowed,
        identity?.GitHubLogin,
        identity?.DisplayName,
        identity?.Email));
}).AllowAnonymous();

app.MapGet("/api/auth/login", (string? returnUrl) =>
{
    if (!authOptions.Enabled)
    {
        return Results.NoContent();
    }

    var redirectUri = IsLocalReturnUrl(returnUrl) ? returnUrl! : "/";
    return Results.Challenge(new AuthenticationProperties { RedirectUri = redirectUri }, [FormicaeAuth.GitHubScheme]);
}).AllowAnonymous();

var logoutEndpoint = app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    if (authOptions.Enabled)
    {
        await context.SignOutAsync(FormicaeAuth.CookieScheme);
    }

    return Results.NoContent();
});

if (authOptions.Enabled)
{
    logoutEndpoint.RequireAuthorization();
}
else
{
    logoutEndpoint.AllowAnonymous();
}

app.MapPost("/api/auth/invite/accept", async (
    AcceptInviteRequest request,
    ClaimsPrincipal user,
    AuthInviteService invites,
    CancellationToken cancellationToken) =>
{
    var identity = FormicaeAuth.GetGitHubIdentity(user);
    if (user.Identity?.IsAuthenticated != true || identity is null)
    {
        return Results.Unauthorized();
    }

    try
    {
        await invites.AcceptInviteAsync(identity, request.InviteCode, cancellationToken);
        return Results.Ok(new { allowed = true });
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).AllowAnonymous();

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
}).RequireAuthorization(FormicaeAuth.ManagementPolicy);

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
}).RequireAuthorization(FormicaeAuth.ManagementPolicy);

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

static bool IsLocalReturnUrl(string? returnUrl)
    => !string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal);

public partial class Program;
