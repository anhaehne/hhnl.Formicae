using System.Text.Json;
using System.Text;

namespace hhnl.Formicae.Application.Workflows;

public sealed class WorkflowExecutionService(IWorkflowStore store, IClock clock)
{
    public Task<WorkflowSearchPage> SearchAsync(WorkflowSearchQuery query, CancellationToken token)
        => store.SearchWorkflowsAsync(WorkflowExecutionQueries.Validate(query), token);
    public Task<WorkflowLogPage> QueryLogsAsync(Guid id, WorkflowLogQuery query, CancellationToken token)
        => store.QueryLogsAsync(id, WorkflowExecutionQueries.Validate(query), token);

    public async Task<WorkflowExecutionResponse?> GetExecutionAsync(Guid id, CancellationToken token)
    {
        var workflow = await store.GetWorkflowAsync(id, token);
        if (workflow is null) return null;
        var version = workflow.WorkflowDefinitionVersionId is { } versionId
            ? await store.GetWorkflowDefinitionVersionAsync(versionId, token) : null;
        var runs = await store.ListTaskRunsAsync(id, token);
        var attempts = await store.ListTaskRunAttemptsAsync(id, token);
        var definition = version is null ? null : WorkflowDefinitionJson.Deserialize(version.DefinitionJson);
        var parallels = new List<WorkflowParallelExecutionResponse>();
        var parallelIds = definition?.Steps.Where(step => step.Uses == WorkflowParallelDefinitions.Uses).Select(step => step.Id).ToHashSet() ?? [];
        foreach (var parallel in await store.ListParallelExecutionsAsync(id, token))
            if (parallelIds.Contains(parallel.NodeId)) parallels.Add(new(parallel.Id, id, parallel.NodeId, parallel.Outcome,
                parallel.StartedAt, parallel.CompletedAt, parallel.VisitIteration));
        var terminal = workflow.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Canceled;
        var cancelling = workflow.CancelRequestedAt is not null;
        var activeWorkers = runs.Any(run => run.RuntimeCleanupPending || run.Status == TaskRunStatus.Running);
        return new(workflow.ToSummary(), version?.Id,
            definition,
            runs.Select(run => run.ToResponse() with { AttemptCount = 1 + attempts.Count(attempt => attempt.TaskRunId == run.Id) }).ToArray(),
            attempts, (await store.ListLoopIterationsAsync(id, token)).Select(item => item.ToResponse()).ToArray(),
            await store.ListDecisionExecutionsAsync(id, token),
            new(workflow.IsPaused, workflow.CancelRequestedAt, workflow.CancelCompletedAt,
                !terminal && !cancelling && !workflow.IsPaused, !terminal && !cancelling && workflow.IsPaused,
                (!terminal || (workflow.Status == WorkflowStatus.Failed && activeWorkers)) && !cancelling),
            (await store.ListEventsAsync(id, token)).Where(evt => evt.Type == "AgentSettingsResolved" && evt.TaskRunId is not null)
                .Select(ReadSettings).OfType<WorkflowResolvedSettings>().ToArray(), parallels, await store.ListWaitsAsync(id, token));
    }

    // Caller holds the scheduler lock, so intent cannot race a worker launch or a transition.
    public async Task<WorkflowSummaryResponse?> SetControlAsync(Guid id, string action, CancellationToken token)
    {
        var workflow = await store.GetWorkflowAsync(id, token);
        if (workflow is null) return null;
        if (action == "cancel" && workflow.CancelRequestedAt is not null) return workflow.ToSummary();
        var canCancelFailed = action == "cancel" && workflow.Status == WorkflowStatus.Failed
            && (await store.ListTaskRunsAsync(id, token)).Any(run => run.RuntimeCleanupPending || run.Status == TaskRunStatus.Running);
        if (workflow.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Canceled && !canCancelFailed)
            throw new InvalidOperationException("Terminal workflows cannot be paused, resumed or canceled.");
        if (workflow.CancelRequestedAt is not null)
            throw new InvalidOperationException("Cancellation is pending.");
        var now = clock.UtcNow;
        var message = action switch
        {
            "pause" => "Workflow paused at the scheduling boundary. Active workers continue producing evidence.",
            "resume" => "Workflow scheduling resumed.",
            "cancel" => "Workflow cancellation requested. Active workers are awaiting termination.",
            _ => throw new ArgumentException("Unknown workflow control.")
        };
        if (action == "cancel") workflow.CancelRequestedAt = now;
        else
        {
            var paused = action == "pause";
            if (workflow.IsPaused == paused) return workflow.ToSummary();
            workflow.IsPaused = paused;
        }
        workflow.UpdatedAt = now;
        await store.UpdateWorkflowAsync(workflow, token);
        await store.AddEventAsync(new WorkflowEvent
        {
            WorkflowId = id, Type = action == "cancel" ? "WorkflowCancelRequested" : action == "pause" ? "WorkflowPaused" : "WorkflowResumed",
            Message = message, CreatedAt = now
        }, token);
        await store.AddLogAsync(new WorkflowLog { WorkflowId = id, Message = message, CreatedAt = now }, token);
        return workflow.ToSummary();
    }

    private static WorkflowResolvedSettings? ReadSettings(WorkflowEvent evt)
    {
        try
        {
            using var document = JsonDocument.Parse(evt.DetailsJson ?? "{}");
            var json = document.RootElement;
            if (json.ValueKind != JsonValueKind.Object) return null;
            JsonElement Field(string key)
            {
                if (json.TryGetProperty(key, out var exact)) return exact;
                foreach (var property in json.EnumerateObject())
                    if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) return property.Value;
                return default;
            }
            string? Text(string key) => Field(key) is var value && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var attempt = Guid.TryParse(Text("executionAttemptId"), out var attemptId) ? attemptId : (Guid?)null;
            var revisionJson = Field("personaRevision");
            var revision = revisionJson.ValueKind == JsonValueKind.Number
                && revisionJson.TryGetInt32(out var number) ? number : (int?)null;
            var environment = Field("environment");
            // This audit payload contains only runtime identity/name/timeout, never the AI settings entity or credentials.
            return new(evt.TaskRunId!.Value, attempt, Text("aiSettingsId"), Text("model"), Text("personaId"), revision, Text("personaName"),
                environment.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? environment.Clone() : null,
                Field("capabilities").ValueKind == JsonValueKind.Array ? JsonSerializer.Deserialize<string[]>(Field("capabilities").GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) : null,
                Field("secretReferences").ValueKind == JsonValueKind.Array ? JsonSerializer.Deserialize<WorkflowSecretReference[]>(Field("secretReferences").GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) : null);
        }
        catch (JsonException) { return null; }
    }

    public async Task<WorkflowEvidenceResponse?> ExportEvidenceAsync(Guid id, CancellationToken token)
    {
        var execution = await GetExecutionAsync(id, token);
        if (execution is null) return null;
        var logs = await DownloadLogsAsync(id, new(), token);
        var events = await store.ListEventsAsync(id, token);
        return new(execution, events.TakeLast(10000).Select(evt => evt.ToResponse()).ToArray(), logs.Items,
            logs.HasEarlier, events.Count > 10000);
    }

    public async Task<WorkflowLogPage> DownloadLogsAsync(Guid id, WorkflowLogQuery query, CancellationToken token)
    {
        var collected = new List<WorkflowLog>();
        var page = await QueryLogsAsync(id, query with { Before = null, After = null, Limit = 1000 }, token);
        collected.AddRange(page.Items);
        var hasEarlier = page.HasEarlier;
        while (hasEarlier && collected.Count < 10000)
        {
            page = await QueryLogsAsync(id, query with { Before = page.PreviousCursor, After = null, Limit = 1000 }, token);
            collected.InsertRange(0, page.Items);
            hasEarlier = page.HasEarlier;
        }
        const int maxBytes = 8 * 1024 * 1024;
        long bytes = 0;
        var bounded = new List<WorkflowLog>();
        foreach (var log in collected.AsEnumerable().Reverse())
        {
            bytes += Encoding.UTF8.GetByteCount(log.Message) + 256;
            if (bytes > maxBytes) { hasEarlier = true; break; }
            bounded.Add(log);
        }
        bounded.Reverse();
        return new(bounded, bounded.LastOrDefault()?.Sequence ?? 0, bounded.FirstOrDefault()?.Sequence ?? 0, false, hasEarlier);
    }
}
