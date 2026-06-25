namespace hhnl.Formicae.Application.Workflows;

public static class WorkflowMapping
{
    public static WorkflowSummaryResponse ToSummary(this Workflow workflow)
        => new(
            workflow.Id,
            workflow.Status,
            workflow.CurrentStep,
            workflow.CreatedAt,
            workflow.UpdatedAt,
            workflow.PullRequestUrl,
            workflow.FailureReason);
}
