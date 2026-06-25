namespace hhnl.Formicae.Application.Workflows;

public enum WorkflowStatus
{
    Queued,
    Planning,
    Implementing,
    CreatingPullRequest,
    Reviewing,
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
    AddressComments,
    Done
}

public enum TaskRunKind
{
    Plan,
    Implement,
    CreatePullRequest,
    AddressComments
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
    string IssueUrl,
    string RepositoryUrl,
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

public static class WorkflowReactionContent
{
    public const string Started = "eyes";
}

public interface IWorkflowTickSignal
{
    void Signal();
}


public static class PullRequestCommentMarkers
{
    private const string Prefix = "<!-- formicae:";

    public static string Plan(Guid workflowId)
        => $"<!-- formicae:workflow:{workflowId:N}:plan -->";

    public static string AddressComments(Guid workflowId)
        => $"<!-- formicae:workflow:{workflowId:N}:address-comments -->";

    public static string PlanRevisionSummary(Guid workflowId)
        => $"<!-- formicae:workflow:{workflowId:N}:plan-revision-summary -->";

    public static bool IsAutomationComment(string body)
        => body.Contains(Prefix, StringComparison.OrdinalIgnoreCase);

    public static string BuildPlanBody(Workflow workflow, AgentRunResult result)
    {
        var marker = Plan(workflow.Id);
        var output = FormatCommentOutput(result.Output);

        return $"""
            {marker}
            Formicae created an implementation plan for workflow `{workflow.Id:N}`.

            {output}
            """;
    }

    public static string BuildAddressCommentsBody(Workflow workflow, AgentRunResult result)
    {
        var marker = AddressComments(workflow.Id);
        var output = FormatCommentOutput(result.Output);

        return $"""
            {marker}
            Formicae addressed the pull request comments for workflow `{workflow.Id:N}`.

            {output}
            """;
    }

    public static string BuildPlanRevisionSummaryBody(Workflow workflow, AgentRunResult result)
    {
        var marker = PlanRevisionSummary(workflow.Id);
        var summary = FormatCommentOutput(ExtractRevisionSummary(result.Output));

        return $"""
            {marker}
            Formicae updated the implementation plan for workflow `{workflow.Id:N}` after new issue feedback.

            {summary}
            """;
    }

    private static string ExtractRevisionSummary(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "The plan comment was updated.";
        }

        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var headingIndex = Array.FindIndex(lines, line => line.Trim().Equals("## Changes from previous plan", StringComparison.OrdinalIgnoreCase));
        if (headingIndex >= 0)
        {
            var collected = new List<string>();
            for (var index = headingIndex + 1; index < lines.Length; index++)
            {
                var line = lines[index];
                if (line.StartsWith("## ", StringComparison.Ordinal) && collected.Count > 0)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line) || collected.Count > 0)
                {
                    collected.Add(line);
                }
            }

            var section = string.Join(Environment.NewLine, collected).Trim();
            if (!string.IsNullOrWhiteSpace(section))
            {
                return section;
            }
        }

        return "The plan comment was updated to account for the latest issue feedback.";
    }

    private static string FormatCommentOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "No additional output was produced.";
        }

        const int maxOutputLength = 60000;
        var trimmed = output.Trim();
        if (trimmed.Length <= maxOutputLength)
        {
            return trimmed;
        }

        return trimmed[..maxOutputLength] + Environment.NewLine + "... output truncated by Formicae ...";
    }
}

public sealed record WorkItem(
    string Url,
    string Title,
    string Body,
    IReadOnlyList<WorkItemComment> Comments,
    IReadOnlyList<string> Labels)
{
    public IReadOnlyList<WorkItemComment> UserComments
        => Comments.Where(comment => !PullRequestCommentMarkers.IsAutomationComment(comment.Body)).ToArray();

    public bool HasLabel(string label)
        => Labels.Any(existing => string.Equals(existing, label, StringComparison.OrdinalIgnoreCase));
}

public sealed record AgentTask(
    Guid WorkflowId,
    TaskRunKind Kind,
    string Prompt,
    string RepositoryUrl,
    string BranchName,
    string? Model,
    IReadOnlyList<AgentTaskContextFile>? ContextFiles = null);

public sealed record AgentTaskContextFile(string FileName, string Content);

public sealed record AgentRunStartResult(string ExternalId, AgentRunResult? CompletedResult = null);

public sealed record AgentRunResult(
    bool Succeeded,
    string ExternalId,
    string Output,
    string? FailureReason);

public sealed record PullRequestResult(string Url);

public sealed record PullRequestComment(
    string Id,
    string Author,
    string Body,
    string Url,
    DateTimeOffset UpdatedAt,
    PullRequestCommentKind Kind);

public enum PullRequestCommentKind
{
    IssueComment,
    ReviewComment
}


public sealed record WorkItemComment(
    string Id,
    string Author,
    string Body,
    string Url,
    DateTimeOffset UpdatedAt);
