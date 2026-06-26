using hhnl.Formicae.Api;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.Configure<GitHubWebhookOptions>(builder.Configuration.GetSection("GitHubWebhooks"));
builder.Services.AddSingleton<WorkflowTickNotifier>();
builder.Services.AddSingleton<IWorkflowTickSignal>(serviceProvider => serviceProvider.GetRequiredService<WorkflowTickNotifier>());
builder.Services.AddScoped<GitHubWebhookHandler>();
builder.Services.AddFormicaeInfrastructure(builder.Configuration);
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

app.MapGet("/api/workflows", async (
    int? limit,
    WorkflowService workflowService,
    CancellationToken cancellationToken) =>
{
    var clampedLimit = Math.Clamp(limit ?? 25, 1, 100);
    return Results.Ok(await workflowService.ListRecentWorkflowsAsync(clampedLimit, cancellationToken));
});

app.MapPost("/api/webhooks/github", async (
    HttpRequest request,
    GitHubWebhookHandler handler,
    CancellationToken cancellationToken) => await handler.HandleAsync(request, cancellationToken));

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
