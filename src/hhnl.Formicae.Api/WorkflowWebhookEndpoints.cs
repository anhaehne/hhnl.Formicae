using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using Microsoft.AspNetCore.Http.Features;

namespace hhnl.Formicae.Api;

public static class WorkflowWebhookEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/webhooks/workflows/{versionId:guid}/{nodeId}", HandleAsync);
        return app;
    }

    private static async Task<IResult> HandleAsync(Guid versionId, string nodeId, HttpRequest request,
        IConfiguration configuration, IWorkflowStore store, WorkflowService workflows,
        IWorkflowOrchestrationLock orchestrationLock, IWorkflowTickSignal signal, IClock clock, CancellationToken token)
    {
        var version = await store.GetWorkflowDefinitionVersionAsync(versionId, token);
        var document = version is { IsEnabled: true } ? WorkflowDefinitionJson.Deserialize(version.DefinitionJson) : null;
        var node = (document is null ? null : WorkflowEventDefinitions.Adapt(document))?.Steps.SingleOrDefault(node => node.Id == nodeId && node.Uses == WorkflowStartDefinitions.Uses
            && node.Trigger is { Type: WorkflowTriggerType.Webhook, Enabled: true });
        if (node is null) return Results.NotFound();
        var secret = WorkflowExecutionExtensions.ValidName(node.Trigger!.WebhookSecretName)
            ? configuration[$"WorkflowWebhooks:Secrets:{node.Trigger.WebhookSecretName}"] : null;
        var authorization = request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(secret) || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(secret)),
                SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..]))))
            return Results.Unauthorized();
        var deliveryId = request.Headers["X-Formicae-Delivery"].ToString();
        if (string.IsNullOrWhiteSpace(deliveryId) || deliveryId.Length > 128)
            return Results.BadRequest(new { error = "X-Formicae-Delivery is required and must be at most 128 characters." });
        if (!request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (request.ContentLength > 65536) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var sizeLimit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeLimit is { IsReadOnly: false }) sizeLimit.MaxRequestBodySize = 65536;
        request.EnableBuffering(bufferThreshold: 65536, bufferLimit: 65536);

        StartGitHubIssueWorkflowRequest? payload;
        try { payload = await request.ReadFromJsonAsync<StartGitHubIssueWorkflowRequest>(token); }
        catch (JsonException) { return Results.BadRequest(new { error = "A valid workflow-start JSON payload is required." }); }
        catch (BadHttpRequestException exception) { return Results.StatusCode(exception.StatusCode); }
        catch (IOException exception) when (exception.Message == "Buffer limit exceeded.")
        { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
        if (payload is null || string.IsNullOrWhiteSpace(payload.IssueUrl) || string.IsNullOrWhiteSpace(payload.RepositoryUrl))
            return Results.BadRequest(new { error = "IssueUrl and RepositoryUrl are required." });

        // Reuse the distributed scheduler lock to make duplicate checking, start and audit creation serial.
        await using var handle = await orchestrationLock.TryAcquireAsync(token);
        if (handle is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var previous = await store.GetTriggerEventByDeliveryAsync(deliveryId, nodeId, token);
        if (previous is not null) return Results.Ok(new { workflowId = previous.WorkflowId, duplicate = true });
        if (await store.GetWorkflowByIssueUrlAsync(payload.IssueUrl, token) is not null)
            return Results.Conflict(new { error = "A workflow already exists for this issue." });

        try
        {
            var started = await workflows.StartGitHubIssueWorkflowAsync(payload with
            {
                WorkflowDefinitionId = version!.WorkflowDefinitionId, WorkflowDefinitionVersionId = version.Id,
                BaseBranch = string.IsNullOrWhiteSpace(node.Trigger.BaseBranch) ? payload.BaseBranch : node.Trigger.BaseBranch,
                Model = string.IsNullOrWhiteSpace(node.Trigger.Model) ? payload.Model : node.Trigger.Model
            }, token, nodeId);
            await store.AddTriggerEventAsync(new WorkflowTriggerEvent
            {
                WorkflowId = started.WorkflowId, WorkflowDefinitionId = version.WorkflowDefinitionId,
                WorkflowDefinitionVersionId = version.Id, TriggerId = nodeId, TriggerType = WorkflowTriggerType.Webhook,
                Provider = "Webhook", ExternalDeliveryId = deliveryId, EventName = "workflow.start", Action = "start",
                PayloadSummaryJson = JsonSerializer.Serialize(new { payload.IssueUrl, payload.RepositoryUrl }), CreatedAt = clock.UtcNow
            }, token);
            signal.Signal();
            return Results.Accepted($"/api/workflows/{started.WorkflowId}", started);
        }
        catch (WorkflowDefinitionValidationException exception) { return Results.BadRequest(new { errors = exception.Errors }); }
        catch (WorkflowDefinitionNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
    }
}
