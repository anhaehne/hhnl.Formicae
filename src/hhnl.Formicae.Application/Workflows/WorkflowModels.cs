using System.Text.Json.Serialization;

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
    public Guid? WorkflowDefinitionId { get; set; }
    public Guid? WorkflowDefinitionVersionId { get; set; }
    public string? DslSchemaVersion { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WorkflowDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WorkflowDefinitionVersion
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid WorkflowDefinitionId { get; init; }
    public int Version { get; init; }
    public required string DslSchemaVersion { get; init; }
    public bool IsEnabled { get; set; }
    public bool IsDefault { get; set; }
    public required string DefinitionJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record WorkflowDefinitionDocument(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("startStepId")] string StartStepId,
    [property: JsonPropertyName("steps")] IReadOnlyList<WorkflowDefinitionStep> Steps);

public sealed record WorkflowDefinitionStep(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("uses")] string Uses,
    [property: JsonPropertyName("nextStepId")] string? NextStepId = null,
    [property: JsonPropertyName("displayName")] string? DisplayName = null);

public sealed class TaskRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid WorkflowId { get; init; }
    public TaskRunKind Kind { get; init; }
    public TaskRunStatus Status { get; set; } = TaskRunStatus.Queued;
    public string? ExternalId { get; set; }
    public string? Output { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WorkflowEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid WorkflowId { get; init; }
    public Guid? TaskRunId { get; init; }
    public required string Type { get; init; }
    public string Level { get; init; } = "Information";
    public required string Message { get; init; }
    public string? DetailsJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
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

public sealed class AiSettings
{
    public const string DefaultId = "default";
    public const string DefaultName = "Default AI";

    public string Id { get; init; } = DefaultId;
    public string Name { get; set; } = DefaultName;
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? EndpointUrl { get; set; }
    public string AgentKind { get; set; } = AgentKinds.OpenHands;
    public string? AcpProvider { get; set; }
    public string? AcpCommand { get; set; }
    public string AuthMethod { get; set; } = OpenHandsAuthMethods.ApiKey;
    public string? LlmApiKeySecretName { get; set; }
    public string? LlmApiKey { get; set; }
    public string? ApiKeyEnvironmentVariable { get; set; }
    public string? SubscriptionCredentialJson { get; set; }
    public string? SubscriptionCredentialFileName { get; set; }
    public string? SubscriptionCredentialMountPath { get; set; }
    public string? CodexAuthJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record StartGitHubIssueWorkflowRequest(
    string IssueUrl,
    string RepositoryUrl,
    string? BaseBranch,
    string? Model,
    Guid? WorkflowDefinitionId = null,
    Guid? WorkflowDefinitionVersionId = null);

public sealed record CreateWorkflowDefinitionRequest(string Name);

public sealed record CreateWorkflowDefinitionVersionRequest(
    int? Version,
    bool IsEnabled,
    bool IsDefault,
    WorkflowDefinitionDocument Definition);

public sealed record WorkflowDefinitionResponse(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<WorkflowDefinitionVersionResponse> Versions);

public sealed record WorkflowDefinitionVersionResponse(
    Guid Id,
    Guid WorkflowDefinitionId,
    int Version,
    string DslSchemaVersion,
    bool IsEnabled,
    bool IsDefault,
    WorkflowDefinitionDocument Definition,
    DateTimeOffset CreatedAt);

public sealed record WorkflowDefinitionValidationError(string Code, string Message, string? Path = null);

public sealed record WorkflowDefinitionValidationResult(IReadOnlyList<WorkflowDefinitionValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static WorkflowDefinitionValidationResult Valid { get; } = new([]);
}

public sealed record AiSettingsResponse(
    string Id,
    string Name,
    string? Provider,
    string? Model,
    string? EndpointUrl,
    string AgentKind,
    string? AcpProvider,
    string? AcpCommand,
    string AuthMethod,
    string? LlmApiKeySecretName,
    bool HasApiKeySecret,
    bool HasApiKey,
    string? ApiKeyEnvironmentVariable,
    bool HasSubscriptionAuth,
    string? SubscriptionCredentialFileName,
    string? SubscriptionCredentialMountPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record UpdateAiSettingsRequest(
    string? Provider = null,
    string? Model = null,
    string? EndpointUrl = null,
    string AuthMethod = OpenHandsAuthMethods.ApiKey,
    string? LlmApiKeySecretName = null,
    string AgentKind = AgentKinds.OpenHands,
    string? AcpProvider = null,
    string? AcpCommand = null,
    string? LlmApiKey = null,
    string? ApiKeyEnvironmentVariable = null,
    string? SubscriptionCredentialJson = null,
    string? SubscriptionCredentialFileName = null,
    string? SubscriptionCredentialMountPath = null,
    string? CodexAuthJson = null,
    string? Id = null,
    string? Name = null);

public static class AgentKinds
{
    public const string OpenHands = "OpenHands";
    public const string Acp = "Acp";
}

public static class AcpProviders
{
    public const string ClaudeCode = "ClaudeCode";
    public const string Codex = "Codex";
    public const string GeminiCli = "GeminiCli";
    public const string Custom = "Custom";
}

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

public sealed record WorkflowEventResponse(
    Guid Id,
    Guid WorkflowId,
    Guid? TaskRunId,
    string Type,
    string Level,
    string Message,
    string? DetailsJson,
    DateTimeOffset CreatedAt);

public sealed record AgentMessageResponse(
    int Sequence,
    string? Role,
    string Content,
    DateTimeOffset? CreatedAt);

public sealed record TaskRunResponse(
    Guid Id,
    Guid WorkflowId,
    TaskRunKind Kind,
    TaskRunStatus Status,
    string? ExternalId,
    string? Output,
    string? FailureReason,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AgentMessageResponse> AgentMessages);

public sealed record WorkflowSignalResponse(
    string Severity,
    string Reason,
    Guid WorkflowId,
    Guid? TaskRunId,
    DateTimeOffset ObservedAt);

public sealed record WorkflowChatMessageResponse(
    string Id,
    string Author,
    string Body,
    string Url,
    DateTimeOffset UpdatedAt);

public static class WorkflowEventTypes
{
    public const string WorkflowQueued = "WorkflowQueued";
    public const string WorkflowTransitioned = "WorkflowTransitioned";
    public const string TaskStarted = "TaskStarted";
    public const string TaskSucceeded = "TaskSucceeded";
    public const string TaskFailed = "TaskFailed";
    public const string ExternalJobAssigned = "ExternalJobAssigned";
    public const string PullRequestCreated = "PullRequestCreated";
    public const string WorkflowCompleted = "WorkflowCompleted";
    public const string WorkflowFailed = "WorkflowFailed";
    public const string ChatCaptured = "ChatCaptured";
}

public static class WorkItemWorkflowLabels
{
    public const string ReadyToPlan = "ready-to-plan";
    public const string ReadyToImplement = "ready-to-implement";
}

public static class WorkflowReactionContent
{
    public const string PlanningStarted = "eyes";
    public const string ImplementationStarted = "rocket";
    public const string FeedbackStarted = "eyes";
    public const string PullRequestCommentStarted = "eyes";
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

public sealed record PullRequestStatus(bool IsOpen, bool IsMerged);

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
