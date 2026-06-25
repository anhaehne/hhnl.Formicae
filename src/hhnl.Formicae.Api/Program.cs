using hhnl.Formicae.Api;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddFormicaeInfrastructure(builder.Configuration);
builder.Services.AddHostedService<WorkflowBackgroundService>();

var app = builder.Build();

if (app.Configuration.GetValue("ApplyDatabaseMigrations", false) && !app.Configuration.GetValue("UseFakeAdapters", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<FormicaeDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.MapHealthChecks("/healthz");

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

app.MapGet("/api/workflows/{workflowId:guid}/logs", async (
    Guid workflowId,
    WorkflowService workflowService,
    CancellationToken cancellationToken) => Results.Ok(await workflowService.ListLogsAsync(workflowId, cancellationToken)));

app.Run();
