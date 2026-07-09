using System.Text.Json;
using hhnl.Formicae.Application.Integrations;

namespace hhnl.Formicae.Application.Workflows;

public sealed class WorkflowTriggerService(
    IWorkflowStore store,
    IDevOpsIntegrationStore integrationStore,
    WorkflowService workflows,
    IClock? clock = null)
{
    private readonly IClock clock = clock ?? new SystemClock();

    public async Task<IReadOnlyList<Guid>> HandleIssueLabelEventAsync(
        DevOpsIssueLabelTriggerEvent evt,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(evt.EventName, "issues", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(evt.Action, "labeled", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(evt.DeliveryId)
            || string.IsNullOrWhiteSpace(evt.RepositoryUrl)
            || string.IsNullOrWhiteSpace(evt.IssueUrl)
            || string.IsNullOrWhiteSpace(evt.Label))
        {
            return [];
        }

        var repositories = await integrationStore.ListAllRepositoriesAsync(cancellationToken);
        var repositoryById = repositories.ToDictionary(repository => repository.Id);
        var enabledVersions = await ListEnabledDefinitionVersionsAsync(cancellationToken);
        var startedWorkflowIds = new List<Guid>();

        foreach (var version in enabledVersions)
        {
            var document = WorkflowDefinitionJson.Deserialize(version.DefinitionJson);
            foreach (var trigger in document?.Triggers ?? [])
            {
                if (!IsMatchingTrigger(trigger, evt, repositoryById, out var repository))
                {
                    continue;
                }

                if (await store.GetTriggerEventByDeliveryAsync(evt.DeliveryId, trigger.Id, cancellationToken) is not null)
                {
                    continue;
                }

                if (await store.GetWorkflowByIssueUrlAsync(evt.IssueUrl, cancellationToken) is not null)
                {
                    continue;
                }

                var workflow = await workflows.StartGitHubIssueWorkflowAsync(new StartGitHubIssueWorkflowRequest(
                    evt.IssueUrl,
                    repository.RepositoryUrl,
                    string.IsNullOrWhiteSpace(trigger.BaseBranch) ? repository.DefaultBranch : trigger.BaseBranch,
                    string.IsNullOrWhiteSpace(trigger.Model) ? null : trigger.Model,
                    version.WorkflowDefinitionId,
                    version.Id), cancellationToken);

                await store.AddTriggerEventAsync(new WorkflowTriggerEvent
                {
                    WorkflowId = workflow.WorkflowId,
                    WorkflowDefinitionId = version.WorkflowDefinitionId,
                    WorkflowDefinitionVersionId = version.Id,
                    TriggerId = trigger.Id,
                    TriggerType = trigger.Type,
                    Provider = evt.ProviderType.ToString(),
                    ExternalDeliveryId = evt.DeliveryId,
                    EventName = evt.EventName,
                    Action = evt.Action,
                    PayloadSummaryJson = JsonSerializer.Serialize(new
                    {
                        evt.RepositoryUrl,
                        evt.RepositoryFullName,
                        evt.IssueUrl,
                        evt.Label
                    }),
                    CreatedAt = clock.UtcNow
                }, cancellationToken);
                startedWorkflowIds.Add(workflow.WorkflowId);
            }
        }

        return startedWorkflowIds;
    }

    private async Task<IReadOnlyList<WorkflowDefinitionVersion>> ListEnabledDefinitionVersionsAsync(CancellationToken cancellationToken)
    {
        var definitions = await store.ListWorkflowDefinitionsAsync(cancellationToken);
        var versions = new List<WorkflowDefinitionVersion>();
        foreach (var definition in definitions)
        {
            versions.AddRange((await store.ListWorkflowDefinitionVersionsAsync(definition.Id, cancellationToken))
                .Where(version => version.IsEnabled));
        }

        return versions
            .OrderByDescending(version => version.IsDefault)
            .ThenByDescending(version => version.CreatedAt)
            .ToArray();
    }

    private static bool IsMatchingTrigger(
        WorkflowDefinitionTrigger trigger,
        DevOpsIssueLabelTriggerEvent evt,
        IReadOnlyDictionary<Guid, ConnectedRepository> repositoryById,
        out ConnectedRepository repository)
    {
        repository = null!;
        if (!trigger.Enabled
            || trigger.Type != WorkflowTriggerType.DevOpsIssueLabel
            || !string.Equals(trigger.Label, evt.Label, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var repositoryId in trigger.RepositoryIds)
        {
            if (!repositoryById.TryGetValue(repositoryId, out var candidate))
            {
                continue;
            }

            if (string.Equals(candidate.RepositoryUrl, evt.RepositoryUrl, StringComparison.OrdinalIgnoreCase))
            {
                repository = candidate;
                return true;
            }
        }

        return false;
    }
}
