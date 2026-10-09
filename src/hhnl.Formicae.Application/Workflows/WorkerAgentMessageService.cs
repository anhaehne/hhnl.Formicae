namespace hhnl.Formicae.Application.Workflows;

public sealed record WorkerAgentMessageRequest(
    Guid WorkflowId, string TaskKind, string ExternalId, string Stream, string Line, DateTimeOffset Timestamp,
    Guid? MessageId = null, Guid? ExecutionAttemptId = null, long? Sequence = null);

public sealed class WorkerAgentMessageService(IWorkflowStore store)
{
    public async Task<bool> RecordAsync(WorkerAgentMessageRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<TaskRunKind>(request.TaskKind, true, out var kind) || !Enum.IsDefined(kind)
            || string.IsNullOrWhiteSpace(request.ExternalId) || string.IsNullOrWhiteSpace(request.Line)
            || request.Sequence is < 0 || request.MessageId == Guid.Empty || request.ExecutionAttemptId == Guid.Empty)
            return false;
        var source = request.Stream?.ToLowerInvariant();
        if (source is not ("stdout" or "stderr" or "worker-error" or "worker" or "worker-checkpoint" or "worker-output-validation" or "worker-output-correction")) return false;
        var run = (await store.ListTaskRunsAsync(request.WorkflowId, cancellationToken)).SingleOrDefault(item =>
            item.Kind == kind && (string.Equals(item.ExternalId, request.ExternalId, StringComparison.Ordinal)
                || (item.ExternalId is null && request.ExecutionAttemptId is { } attempt && item.ExecutionAttemptId == attempt)));
        if (run is null) return false;
        // The store rechecks identity under its append lock. Callbacks never own authoritative task output.
        return await store.TryAddWorkerLogAsync(new WorkflowLog
        {
            WorkflowId = request.WorkflowId, TaskRunId = run.Id,
            Id = request.MessageId ?? Guid.NewGuid(), ExecutionAttemptId = request.ExecutionAttemptId ?? run.ExecutionAttemptId,
            ExternalId = request.ExternalId, Source = source, SourceSequence = request.Sequence,
            Level = source == "worker-error" ? "Error" : source == "stderr" ? "Warning" : "Information",
            Message = request.Line.Length > 16000 ? request.Line[..16000] + "\n[Message truncated]" : request.Line,
            CreatedAt = request.Timestamp
        }, request.ExternalId, request.ExecutionAttemptId, cancellationToken);
    }
}
