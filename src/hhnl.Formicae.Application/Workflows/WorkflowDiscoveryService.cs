using hhnl.Formicae.Application.Integrations;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Application.Workflows;

public sealed class WorkflowDiscoveryService(
    IWorkflowStore store,
    IWorkItemProvider workItems,
    IOptions<WorkflowDiscoveryOptions> options,
    AiSettingsService? aiSettingsService = null,
    IDevOpsIntegrationStore? integrationStore = null)
{
    public async Task<int> DiscoverReadyToPlanWorkflowsAsync(CancellationToken cancellationToken)
    {
        var discovery = options.Value;
        if (!discovery.Enabled)
        {
            return 0;
        }

        var repositories = await GetDiscoveryRepositoriesAsync(discovery, cancellationToken);
        var created = 0;
        var model = aiSettingsService is null
            ? discovery.Model
            : (await aiSettingsService.ResolveAsync(cancellationToken)).Model ?? discovery.Model;

        foreach (var repository in repositories)
        {
            var issues = await workItems.ListIssuesWithLabelAsync(
                repository.RepositoryUrl,
                WorkItemWorkflowLabels.ReadyToPlan,
                cancellationToken);

            foreach (var issue in issues)
            {
                if (await store.GetWorkflowByIssueUrlAsync(issue.Url, cancellationToken) is not null)
                {
                    continue;
                }

                var workflow = new Workflow
                {
                    IssueUrl = issue.Url,
                    RepositoryUrl = repository.RepositoryUrl,
                    BaseBranch = string.IsNullOrWhiteSpace(repository.DefaultBranch) ? "main" : repository.DefaultBranch,
                    Model = model,
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
        }

        return created;
    }

    private async Task<IReadOnlyList<DiscoveryRepository>> GetDiscoveryRepositoriesAsync(
        WorkflowDiscoveryOptions discovery,
        CancellationToken cancellationToken)
    {
        var repositories = new Dictionary<string, DiscoveryRepository>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(discovery.RepositoryUrl))
        {
            repositories[discovery.RepositoryUrl] = new DiscoveryRepository(discovery.RepositoryUrl, discovery.BaseBranch);
        }

        if (integrationStore is not null)
        {
            foreach (var repository in await integrationStore.ListAllRepositoriesAsync(cancellationToken))
            {
                repositories[repository.RepositoryUrl] = new DiscoveryRepository(repository.RepositoryUrl, repository.DefaultBranch);
            }
        }

        return repositories.Values.ToArray();
    }

    private sealed record DiscoveryRepository(string RepositoryUrl, string DefaultBranch);
}
