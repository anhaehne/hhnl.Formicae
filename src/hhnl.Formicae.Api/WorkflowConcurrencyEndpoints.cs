using hhnl.Formicae.Application.Management;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Api;

public static class WorkflowConcurrencyEndpoints
{
    public static WebApplication MapWorkflowConcurrencyEndpoints(this WebApplication app)
    {
        app.MapGet("/api/workflow-concurrency", async (IWorkflowStore store, CancellationToken token)
            => Results.Ok(await store.GetConcurrencySettingsAsync(token)))
            .RequireAuthorization(ManagementAuthorization.WorkflowView);
        app.MapPut("/api/workflow-concurrency", async (WorkflowConcurrencyLimit request, IWorkflowStore store,
            IWorkflowOrchestrationLock orchestrationLock, IWorkflowTickSignal tick, CancellationToken token) =>
        {
            if (request.MaxActiveInstances is <= 0) return Results.BadRequest(new { error = "Limit must be a positive integer or null for unlimited." });
            await using var handle = await orchestrationLock.TryAcquireAsync(token);
            if (handle is null) return Results.Conflict(new { error = "Workflow scheduling is in progress. Retry shortly." });
            var settings = new WorkflowConcurrencySettings { MaxActiveInstances = request.MaxActiveInstances };
            await store.SetConcurrencySettingsAsync(settings, token);
            tick.Signal();
            return Results.Ok(settings);
        }).RequireAuthorization(ManagementAuthorization.ManagementAdmin);
        app.MapPut("/api/workflow-definitions/{definitionId:guid}/concurrency", async (Guid definitionId,
            WorkflowConcurrencyLimit request, IWorkflowStore store, IWorkflowOrchestrationLock orchestrationLock,
            IWorkflowTickSignal tick, CancellationToken token) =>
        {
            if (request.MaxActiveInstances is <= 0) return Results.BadRequest(new { error = "Limit must be a positive integer or null for unlimited." });
            await using var handle = await orchestrationLock.TryAcquireAsync(token);
            if (handle is null) return Results.Conflict(new { error = "Workflow scheduling is in progress. Retry shortly." });
            if (await store.GetWorkflowDefinitionAsync(definitionId, token) is null) return Results.NotFound();
            await store.SetDefinitionConcurrencyAsync(definitionId, request.MaxActiveInstances, token);
            tick.Signal();
            return Results.Ok(request);
        }).RequireAuthorization(ManagementAuthorization.ManagementAdmin);
        return app;
    }
}
