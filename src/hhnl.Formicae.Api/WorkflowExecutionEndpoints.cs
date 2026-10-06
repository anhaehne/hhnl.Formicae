using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using Microsoft.AspNetCore.Mvc;

namespace hhnl.Formicae.Api;

public static class WorkflowExecutionEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowExecutionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/workflows/search", async ([AsParameters] WorkflowSearchQuery query, WorkflowExecutionService service, CancellationToken token) =>
        {
            try { return Results.Ok(await service.SearchAsync(query, token)); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        }).RequireAuthorization(ManagementAuthorization.WorkflowView);
        app.MapGet("/api/workflows/{workflowId:guid}/execution", async (Guid workflowId, WorkflowExecutionService service, CancellationToken token) =>
        {
            var execution = await service.GetExecutionAsync(workflowId, token);
            return execution is null ? Results.NotFound() : Results.Ok(execution);
        }).RequireAuthorization(ManagementAuthorization.WorkflowView);
        app.MapGet("/api/workflows/{workflowId:guid}/logs/page", async (Guid workflowId, [AsParameters] WorkflowLogQuery query,
            WorkflowExecutionService service, IWorkflowStore store, CancellationToken token) =>
        {
            if (await store.GetWorkflowAsync(workflowId, token) is null) return Results.NotFound();
            try { return Results.Ok(await service.QueryLogsAsync(workflowId, query, token)); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        }).RequireAuthorization(ManagementAuthorization.WorkflowView);
        app.MapGet("/api/workflows/{workflowId:guid}/logs/stream", async (Guid workflowId, [AsParameters] WorkflowLogQuery query,
            HttpContext context, IWorkflowStore store, IServiceScopeFactory scopes, CancellationToken token) =>
        {
            if (await store.GetWorkflowAsync(workflowId, token) is null) return Results.NotFound();
            var lastId = context.Request.Headers["Last-Event-ID"].ToString();
            if (lastId.Length > 0)
            {
                if (!long.TryParse(lastId, NumberStyles.None, CultureInfo.InvariantCulture, out var cursor))
                    return Results.BadRequest(new { error = "Last-Event-ID must be a nonnegative log cursor." });
                query = query with { After = cursor, Before = null };
            }
            try { query = WorkflowExecutionQueries.Validate(query with { After = query.After ?? 0, Limit = 200 }); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            return Results.ServerSentEvents(StreamLogsAsync(workflowId, query, scopes, token));
        }).RequireAuthorization(ManagementAuthorization.WorkflowView);
        app.MapGet("/api/workflows/{workflowId:guid}/logs/download", async (Guid workflowId, [AsParameters] WorkflowLogQuery query,
            WorkflowExecutionService service, IWorkflowStore store, CancellationToken token) =>
        {
            if (await store.GetWorkflowAsync(workflowId, token) is null) return Results.NotFound();
            try
            {
                WorkflowExecutionQueries.Validate(query);
                var page = await service.DownloadLogsAsync(workflowId, query, token);
                var body = string.Join('\n', page.Items.Select(log => $"{log.CreatedAt:O} [{log.Level}] [{log.Source}] [task:{log.TaskRunId}] [attempt:{log.ExecutionAttemptId}] {WorkflowEvidenceSanitizer.RedactText(log.Message)}"));
                if (page.HasEarlier) body = "[Export is bounded to 10000 records and 8 MiB; earlier records are omitted.]\n" + body;
                return Results.File(Encoding.UTF8.GetBytes(body), "text/plain; charset=utf-8", $"workflow-{workflowId}-logs.txt");
            }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        }).RequireAuthorization(ManagementAuthorization.WorkflowView);
        app.MapGet("/api/workflows/{workflowId:guid}/evidence", async (Guid workflowId, WorkflowExecutionService service, CancellationToken token) =>
        {
            var evidence = await service.ExportEvidenceAsync(workflowId, token);
            if (evidence is null) return Results.NotFound();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(WorkflowEvidenceSanitizer.Sanitize(evidence));
            if (bytes.Length > 16 * 1024 * 1024)
                return Results.Problem("The evidence exceeds the 16 MiB export limit. Download filtered task logs instead.", statusCode: 413);
            return Results.File(bytes, "application/json", $"workflow-{workflowId}-evidence.json");
        }).RequireAuthorization(ManagementAuthorization.WorkflowView);
        foreach (var action in new[] { "pause", "resume", "cancel" })
        {
            var operation = action;
            app.MapPost($"/api/workflows/{{workflowId:guid}}/{operation}", async (Guid workflowId, WorkflowExecutionService service,
                IWorkflowOrchestrationLock orchestrationLock, IWorkflowTickSignal tick, CancellationToken token) =>
            {
                await using var handle = await orchestrationLock.TryAcquireAsync(token);
                if (handle is null) return Results.Conflict(new { error = "Workflow scheduling is in progress. Retry shortly." });
                try
                {
                    var workflow = await service.SetControlAsync(workflowId, operation, token);
                    if (workflow is null) return Results.NotFound();
                    tick.Signal();
                    return Results.Ok(workflow);
                }
                catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
            }).RequireAuthorization(ManagementAuthorization.WorkflowOperate);
        }
        return app;
    }

    public static async IAsyncEnumerable<SseItem<object>> StreamLogsAsync(Guid id, WorkflowLogQuery query,
        IServiceScopeFactory scopes, [EnumeratorCancellation] CancellationToken token)
    {
        var cursor = query.After ?? 0;
        var heartbeatAt = DateTimeOffset.MinValue;
        while (!token.IsCancellationRequested)
        {
            WorkflowLogPage page;
            await using (var scope = scopes.CreateAsyncScope())
                page = await scope.ServiceProvider.GetRequiredService<WorkflowExecutionService>()
                    .QueryLogsAsync(id, query with { After = cursor, Before = null }, token);
            foreach (var log in page.Items)
            {
                cursor = log.Sequence;
                yield return new SseItem<object>(log, "log")
                {
                    EventId = cursor.ToString(CultureInfo.InvariantCulture), ReconnectionInterval = TimeSpan.FromSeconds(2)
                };
            }
            if (DateTimeOffset.UtcNow >= heartbeatAt)
            {
                yield return new SseItem<object>(new { cursor }, "heartbeat") { ReconnectionInterval = TimeSpan.FromSeconds(2) };
                heartbeatAt = DateTimeOffset.UtcNow.AddSeconds(10);
            }
            if (!page.HasMore) await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
    }
}
