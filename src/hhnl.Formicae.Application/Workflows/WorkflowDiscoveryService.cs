using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Application.Workflows;

public sealed class WorkflowDiscoveryService(
    IWorkflowStore store,
    IWorkItemProvider workItems,
    IOptions<WorkflowDiscoveryOptions> options)
{
    public async Task<int> DiscoverReadyToPlanWorkflowsAsync(CancellationToken cancellationToken)
    {
        var discovery = options.Value;
        if (!discovery.Enabled || string.IsNullOrWhiteSpace(discovery.RepositoryUrl))
        {
            return 0;
        }

        var issues = await workItems.ListIssuesWithLabelAsync(
            discovery.RepositoryUrl,
            WorkItemWorkflowLabels.ReadyToPlan,
            cancellationToken);
        var created = 0;

        foreach (var issue in issues)
        {
            if (await store.GetWorkflowByIssueUrlAsync(issue.Url, cancellationToken) is not null)
            {
                continue;
            }

            var workflow = new Workflow
            {
                IssueUrl = issue.Url,
                RepositoryUrl = discovery.RepositoryUrl,
                BaseBranch = string.IsNullOrWhiteSpace(discovery.BaseBranch) ? "main" : discovery.BaseBranch,
                Model = discovery.Model,
                Status = WorkflowStatus.Queued,
                CurrentStep = WorkflowStep.None
            };

            await store.CreateWorkflowAsync(workflow, cancellationToken);
            await store.AddLogAsync(new WorkflowLog
            {
                WorkflowId = workflow.Id,
                Message = $"Workflow queued from GitHub issue label '{WorkItemWorkflowLabels.ReadyToPlan}'."
            }, cancellationToken);
            created++;
        }

        return created;
    }
}
