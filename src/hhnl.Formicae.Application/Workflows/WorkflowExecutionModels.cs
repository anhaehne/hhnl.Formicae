namespace hhnl.Formicae.Application.Workflows;

public sealed record AgentRuntimeLog(string Source, string Message, DateTimeOffset Timestamp, Guid? MessageId = null, long? SourceSequence = null);

public sealed record WorkflowSearchQuery(string? Search = null, WorkflowStatus? Status = null,
    string? RepositoryUrl = null, Guid? DefinitionId = null, DateTimeOffset? From = null, DateTimeOffset? To = null,
    int Offset = 0, int Limit = 50, bool Active = false);
public sealed record WorkflowSearchPage(IReadOnlyList<WorkflowSummaryResponse> Items, int TotalCount, int Offset, int Limit);
public sealed record WorkflowLogQuery(long? After = null, long? Before = null, Guid? TaskRunId = null,
    Guid? ExecutionAttemptId = null, string? Level = null, string? Source = null, string? Search = null, int Limit = 200);
public sealed record WorkflowLogPage(IReadOnlyList<WorkflowLog> Items, long NextCursor, long PreviousCursor, bool HasMore, bool HasEarlier);

// Immutable evidence from the attempt replaced by a retry, including the prepared inputs and provenance.
public sealed class TaskRunAttempt
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid WorkflowId { get; init; }
    public Guid TaskRunId { get; init; }
    public Guid ExecutionAttemptId { get; init; }
    public int AttemptNumber { get; init; }
    public required string DefinitionStepId { get; init; }
    public int? LoopIteration { get; init; }
    public TaskRunStatus Status { get; init; }
    public string? ExternalId { get; init; }
    public string? Output { get; init; }
    public string? FailureReason { get; init; }
    public string? StructuredOutputsJson { get; init; }
    public string? CustomTaskExecutionJson { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public static TaskRunAttempt Snapshot(TaskRun run, int number) => new()
    {
        WorkflowId = run.WorkflowId, TaskRunId = run.Id, ExecutionAttemptId = run.ExecutionAttemptId ?? run.Id,
        AttemptNumber = number, DefinitionStepId = run.DefinitionStepId, LoopIteration = run.LoopIteration,
        Status = run.Status, ExternalId = run.ExternalId, Output = run.Output, FailureReason = run.FailureReason,
        StructuredOutputsJson = run.StructuredOutputsJson, CustomTaskExecutionJson = run.CustomTaskExecutionJson,
        StartedAt = run.StartedAt, CompletedAt = run.CompletedAt, CreatedAt = run.CreatedAt, UpdatedAt = run.UpdatedAt
    };
}
public sealed record WorkflowExecutionControl(bool IsPaused, DateTimeOffset? CancelRequestedAt,
    DateTimeOffset? CancelCompletedAt, bool CanPause, bool CanResume, bool CanCancel);
public sealed record WorkflowExecutionResponse(WorkflowSummaryResponse Workflow, Guid? DefinitionVersionId,
    WorkflowDefinitionDocument? Definition, IReadOnlyList<TaskRunResponse> Runs, IReadOnlyList<TaskRunAttempt> Attempts,
    IReadOnlyList<WorkflowLoopIterationResponse> Loops, IReadOnlyList<WorkflowDecisionExecution> Decisions,
    WorkflowExecutionControl Control, IReadOnlyList<WorkflowResolvedSettings> ResolvedSettings,
    IReadOnlyList<WorkflowParallelExecutionResponse> Parallels);
public sealed record WorkflowParallelExecutionResponse(Guid Id, Guid WorkflowId, string NodeId,
    WorkflowParallelExecutionOutcome Outcome, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt);
public sealed record WorkflowResolvedSettings(Guid TaskRunId, Guid? ExecutionAttemptId, string? AiSettingsId, string? Model,
    string? PersonaId, int? PersonaRevision, string? PersonaName, System.Text.Json.JsonElement? Environment);
public sealed record WorkflowEvidenceResponse(WorkflowExecutionResponse Execution, IReadOnlyList<WorkflowEventResponse> Events,
    IReadOnlyList<WorkflowLog> Logs, bool LogsTruncated, bool EventsTruncated);
