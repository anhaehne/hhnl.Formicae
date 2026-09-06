using System.Text.Json;

namespace hhnl.Formicae.Application.Workflows;

public sealed record WorkerAgentMessageRequest(
    Guid WorkflowId,
    string TaskKind,
    string ExternalId,
    string Stream,
    string Line,
    DateTimeOffset Timestamp);

public sealed class WorkerAgentMessageService(IWorkflowStore store)
{
    public async Task<bool> RecordAsync(WorkerAgentMessageRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<TaskRunKind>(request.TaskKind, ignoreCase: true, out var taskKind)
            || !Enum.IsDefined(taskKind) || string.IsNullOrWhiteSpace(request.ExternalId))
        {
            return false;
        }

        var workflow = await store.GetWorkflowAsync(request.WorkflowId, cancellationToken);
        if (workflow is null)
        {
            return false;
        }

        var run = (await store.ListTaskRunsAsync(request.WorkflowId, cancellationToken))
            .SingleOrDefault(item => item.Kind == taskKind
                && string.Equals(item.ExternalId, request.ExternalId, StringComparison.Ordinal));
        if (run is null)
        {
            return false;
        }

        if (taskKind != TaskRunKind.Custom && TryNormalizeAgentOutputLine(request.Line, request.Timestamp, out var outputLine))
        {
            run.Output = string.IsNullOrWhiteSpace(run.Output)
                ? outputLine
                : run.Output.TrimEnd() + Environment.NewLine + outputLine;
            run.UpdatedAt = request.Timestamp;
            await store.UpsertTaskRunAsync(run, cancellationToken);
        }

        if (taskKind == TaskRunKind.Custom || string.Equals(request.Stream, "stderr", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Stream, "worker-error", StringComparison.OrdinalIgnoreCase))
        {
            await store.AddLogAsync(new WorkflowLog
            {
                WorkflowId = request.WorkflowId,
                TaskRunId = run.Id,
                Level = string.Equals(request.Stream, "worker-error", StringComparison.OrdinalIgnoreCase) ? "Error"
                    : string.Equals(request.Stream, "stderr", StringComparison.OrdinalIgnoreCase) ? "Warning" : "Information",
                Message = taskKind == TaskRunKind.Custom && request.Line.Length > 16000 ? request.Line[..16000] + "\n[Message truncated]" : request.Line,
                CreatedAt = request.Timestamp
            }, cancellationToken);
        }

        return true;
    }

    private static bool TryNormalizeAgentOutputLine(string line, DateTimeOffset timestamp, out string outputLine)
    {
        outputLine = string.Empty;
        if (!line.TrimStart().StartsWith('{'))
        {
            return false;
        }

        if (AgentMessageParser.Parse(line).Count > 0)
        {
            outputLine = line;
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!TryGetString(root, "type", out var eventType)
                || !string.Equals(eventType, "item.completed", StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("item", out var item)
                || !TryGetString(item, "type", out var itemType)
                || !string.Equals(itemType, "agent_message", StringComparison.OrdinalIgnoreCase)
                || !TryGetString(item, "text", out var text)
                || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            outputLine = JsonSerializer.Serialize(new
            {
                type = "agent_message",
                message = text,
                timestamp
            });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }
}
