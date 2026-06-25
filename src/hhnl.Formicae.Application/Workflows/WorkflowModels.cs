namespace hhnl.Formicae.Application.Workflows;

public enum WorkflowStatus
{
    Queued,
    Planning,
    Implementing,
    CreatingPullRequest,
    Completed,
    Failed,
    Canceled
}

public enum WorkflowStep
{
    None,
    Plan,
    Implement,
    CreatePullRequest,
    Done
}

public enum TaskRunKind
{
    Plan,
    Implement,
    CreatePullRequest
}

public enum TaskRunStatus
{
    Queued,
    Running,
    Succeeded,
    Failed
}

public sealed class Workflow
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string IssueUrl { get; init; }
    public required string RepositoryUrl { get; init; }
    public string BaseBranch { get; set; } = "main";
    public string? Model { get; set; }
    public WorkflowStatus Status { get; set; } = WorkflowStatus.Queued;
    public WorkflowStep CurrentStep { get; set; } = WorkflowStep.None;
    public string? BranchName { get; set; }
    public string? PlanArtifact { get; set; }
    public string? PullRequestUrl { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TaskRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid WorkflowId { get; init; }
    public TaskRunKind Kind { get; init; }
    public TaskRunStatus Status { get; set; } = TaskRunStatus.Queued;
    public string? ExternalId { get; set; }
    public string? Output { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WorkflowLog
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid WorkflowId { get; init; }
    public Guid? TaskRunId { get; init; }
    public string Level { get; init; } = "Information";
    public required string Message { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record StartGitHubIssueWorkflowRequest(
    string IssueUrl,
    string RepositoryUrl,
    string? BaseBranch,
    string? Model);

public sealed record WorkflowSummaryResponse(
    Guid WorkflowId,
    WorkflowStatus Status,
    WorkflowStep CurrentStep,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PullRequestUrl,
    string? FailureReason);

public static class WorkItemWorkflowLabels
{
    public const string ReadyToPlan = "ready-to-plan";
    public const string ReadyToImplement = "ready-to-implement";
}

public sealed record WorkItem(
    string Url,
    string Title,
    string Body,
    IReadOnlyList<string> Comments,
    IReadOnlyList<string> Labels)
{
    public bool HasLabel(string label)
        => Labels.Any(existing => string.Equals(existing, label, StringComparison.OrdinalIgnoreCase));
}

public sealed record AgentTask(
    Guid WorkflowId,
    TaskRunKind Kind,
    string Prompt,
    string RepositoryUrl,
    string BranchName,
    string? Model);

public sealed record AgentRunResult(
    bool Succeeded,
    string ExternalId,
    string Output,
    string? FailureReason);

public sealed record PullRequestResult(string Url);
