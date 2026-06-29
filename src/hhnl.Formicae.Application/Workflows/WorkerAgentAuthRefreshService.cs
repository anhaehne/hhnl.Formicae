using System.Text.Json;

namespace hhnl.Formicae.Application.Workflows;

public sealed record WorkerAgentAuthRefreshRequest(
    Guid WorkflowId,
    string TaskKind,
    string ExternalId,
    string AiSettingsId,
    string CodexAuthJson);

public sealed class WorkerAgentAuthRefreshService(IWorkflowStore workflowStore, AiSettingsService aiSettingsService)
{
    public async Task<bool> RecordAsync(WorkerAgentAuthRefreshRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<TaskRunKind>(request.TaskKind, ignoreCase: true, out var taskKind)
            || string.IsNullOrWhiteSpace(request.ExternalId)
            || string.IsNullOrWhiteSpace(request.AiSettingsId)
            || string.IsNullOrWhiteSpace(request.CodexAuthJson))
        {
            return false;
        }

        try
        {
            using var _ = JsonDocument.Parse(request.CodexAuthJson);
        }
        catch (JsonException)
        {
            return false;
        }

        var run = await workflowStore.GetTaskRunAsync(request.WorkflowId, taskKind, cancellationToken);
        if (run is null
            || !string.Equals(run.ExternalId, request.ExternalId, StringComparison.Ordinal))
        {
            return false;
        }

        return await aiSettingsService.UpdateCodexAuthAsync(request.AiSettingsId, request.CodexAuthJson, cancellationToken);
    }
}
