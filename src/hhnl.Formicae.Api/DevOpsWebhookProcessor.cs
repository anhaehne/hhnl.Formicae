using System.Text.Json;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Api;

public sealed class DevOpsWebhookProcessor(IWorkflowStore store)
{
    public static bool ShouldTriggerWorkflowTick(string eventName, string action, bool issueCommentIsPullRequest)
        => eventName switch
        {
            "issues" => IsOneOf(action, "opened", "edited", "reopened", "labeled", "unlabeled"),
            "issue_comment" => IsOneOf(action, "created", "edited") || issueCommentIsPullRequest && IsOneOf(action, "deleted"),
            "pull_request" => IsOneOf(action, "opened", "reopened", "synchronize", "ready_for_review", "converted_to_draft", "closed"),
            "pull_request_review_comment" => IsOneOf(action, "created", "edited"),
            "pull_request_review" => IsOneOf(action, "submitted", "edited", "dismissed"),
            _ => false
        };

    public async Task<DevOpsWebhookProcessingResult> ProcessAsync(
        DevOpsWebhookEvent webhookEvent,
        CancellationToken cancellationToken)
    {
        var completedWorkflowId = await CompleteMergedPullRequestWorkflowAsync(webhookEvent, cancellationToken);
        var requeuedWorkflowId = await RequeueCompletedPullRequestWorkflowAsync(webhookEvent, cancellationToken);
        return new DevOpsWebhookProcessingResult(completedWorkflowId, requeuedWorkflowId);
    }

    private async Task<Guid?> CompleteMergedPullRequestWorkflowAsync(
        DevOpsWebhookEvent webhookEvent,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(webhookEvent.EventName, "pull_request", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(webhookEvent.Action, "closed", StringComparison.OrdinalIgnoreCase)
            || webhookEvent.PullRequestMerged != true
            || string.IsNullOrWhiteSpace(webhookEvent.PullRequestUrl))
        {
            return null;
        }

        var workflow = await store.GetWorkflowByPullRequestUrlAsync(webhookEvent.PullRequestUrl, cancellationToken);
        if (workflow is null || workflow.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Canceled)
        {
            return null;
        }

        workflow.Status = WorkflowStatus.Completed;
        workflow.CurrentStep = WorkflowStep.Done;
        workflow.FailureReason = null;
        workflow.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpdateWorkflowAsync(workflow, cancellationToken);
        await store.AddEventAsync(new WorkflowEvent
        {
            WorkflowId = workflow.Id,
            Type = WorkflowEventTypes.WorkflowCompleted,
            Message = "Workflow completed because the pull request was merged.",
            DetailsJson = JsonSerializer.Serialize(new { pullRequestUrl = workflow.PullRequestUrl, webhookEvent.EventName, webhookEvent.Action, webhookEvent.Provider }),
            CreatedAt = workflow.UpdatedAt
        }, cancellationToken);
        await store.AddLogAsync(new WorkflowLog
        {
            WorkflowId = workflow.Id,
            Message = $"Workflow marked completed from {webhookEvent.Provider} pull_request closed/merged webhook."
        }, cancellationToken);
        return workflow.Id;
    }

    private async Task<Guid?> RequeueCompletedPullRequestWorkflowAsync(
        DevOpsWebhookEvent webhookEvent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webhookEvent.CommentPullRequestUrl))
        {
            return null;
        }

        var workflow = await store.GetWorkflowByPullRequestUrlAsync(webhookEvent.CommentPullRequestUrl, cancellationToken);
        if (workflow?.Status != WorkflowStatus.Completed)
        {
            return null;
        }

        workflow.Status = WorkflowStatus.Reviewing;
        workflow.CurrentStep = WorkflowStep.AddressComments;
        workflow.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpdateWorkflowAsync(workflow, cancellationToken);
        await store.AddLogAsync(new WorkflowLog
        {
            WorkflowId = workflow.Id,
            Message = $"Workflow requeued from {webhookEvent.Provider} {webhookEvent.EventName} webhook for pull request comments."
        }, cancellationToken);
        return workflow.Id;
    }

    private static bool IsOneOf(string value, params string[] expectedValues)
    {
        foreach (var expected in expectedValues)
        {
            if (string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed record DevOpsWebhookEvent(
    string Provider,
    string EventName,
    string Action,
    bool IssueCommentIsPullRequest,
    string? PullRequestUrl,
    bool? PullRequestMerged,
    string? CommentPullRequestUrl);

public sealed record DevOpsWebhookProcessingResult(Guid? CompletedWorkflowId, Guid? RequeuedWorkflowId);
