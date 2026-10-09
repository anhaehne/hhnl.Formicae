using System.Text.Json;
using hhnl.Formicae.Application.Integrations;

namespace hhnl.Formicae.Application.Workflows;

public sealed class WorkflowEventService(
    IWorkflowStore store,
    IDevOpsIntegrationStore integrationStore,
    WorkflowService workflows,
    IClock? clock = null)
{
    private readonly IClock clock = clock ?? new SystemClock();

    public Task<IReadOnlyList<Guid>> HandleIssueEventAsync(DevOpsIssueLabelTriggerEvent delivery, CancellationToken token)
        => HandleIntegrationEventAsync(new(delivery.ProviderType, delivery.DeliveryId, delivery.EventName, delivery.Action,
            delivery.RepositoryUrl, delivery.IssueUrl, delivery.Label, delivery.RepositoryFullName), token);

    public async Task<IReadOnlyList<Guid>> HandleIntegrationEventAsync(
        WorkflowIntegrationEvent evt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(evt.DeliveryId)
            || string.IsNullOrWhiteSpace(evt.RepositoryUrl)
            || string.IsNullOrWhiteSpace(evt.IssueUrl))
        {
            return [];
        }

        var repositories = await integrationStore.ListAllRepositoriesAsync(cancellationToken);
        var repositoryById = repositories.ToDictionary(repository => repository.Id);
        var enabledVersions = await ListEnabledDefinitionVersionsAsync(cancellationToken);
        var startedWorkflowIds = new List<Guid>();

        foreach (var version in enabledVersions)
        {
            var rawDocument = WorkflowDefinitionJson.Deserialize(version.DefinitionJson);
            if (rawDocument is null || !new WorkflowDefinitionValidator().Validate(rawDocument).IsValid) continue;
            var document = WorkflowNodeDefinitions.Normalize(rawDocument);
            foreach (var trigger in document?.Triggers ?? [])
            {
                var source = rawDocument.Steps.FirstOrDefault(node => node.Id == trigger.Id);
                // Concrete integrations own matching. The old enum is consulted only for legacy documents.
                var matches = source?.Event is { } settings && WorkflowEventRegistry.Default.TryGet(source.Uses, out var eventDefinition)
                    ? eventDefinition.Matches(settings, evt) : MatchesLegacyEvent(trigger, evt);
                if (!matches || !trigger.Enabled || !TryFindRepository(trigger, evt, repositoryById, out var repository)) continue;

                if (await store.GetTriggerEventByDeliveryAsync(evt.DeliveryId, trigger.Id, cancellationToken) is not null)
                {
                    continue;
                }

                if (await store.GetWorkflowByIssueUrlAsync(evt.IssueUrl, cancellationToken) is not null)
                {
                    continue;
                }

                IReadOnlyDictionary<string, JsonElement>? outputs = null;
                if (trigger.Type == WorkflowTriggerType.DevOpsIssueCreated && evt.Issue is { ValueKind: JsonValueKind.Object } issue)
                {
                    if (!issue.TryGetProperty("number", out var number) || !number.TryGetInt32(out var issueId) || issueId <= 0)
                        throw new InvalidOperationException("GitHub Issue created requires a positive issue number.");
                    outputs = new Dictionary<string, JsonElement>
                    {
                        ["issue"] = JsonSerializer.SerializeToElement(issue.GetRawText()),
                        ["issueId"] = number.Clone()
                    };
                }
                var workflow = await workflows.StartGitHubIssueWorkflowAsync(new StartGitHubIssueWorkflowRequest(
                    evt.IssueUrl,
                    repository.RepositoryUrl,
                    string.IsNullOrWhiteSpace(trigger.BaseBranch) ? repository.DefaultBranch : trigger.BaseBranch,
                    string.IsNullOrWhiteSpace(trigger.Model) ? null : trigger.Model,
                    version.WorkflowDefinitionId,
                    version.Id), cancellationToken, trigger.Id, outputs);

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

    private static bool MatchesLegacyEvent(WorkflowDefinitionTrigger trigger, WorkflowIntegrationEvent evt)
        => string.Equals(evt.EventName, "issues", StringComparison.OrdinalIgnoreCase)
            && (trigger.Type == WorkflowTriggerType.DevOpsIssueLabel && string.Equals(evt.Action, "labeled", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(evt.Label) && string.Equals(trigger.Label, evt.Label, StringComparison.OrdinalIgnoreCase)
                || trigger.Type == WorkflowTriggerType.DevOpsIssueCreated && evt.ProviderType == DevOpsProviderType.GitHub
                    && string.Equals(evt.Action, "opened", StringComparison.OrdinalIgnoreCase));

    private static bool TryFindRepository(
        WorkflowDefinitionTrigger trigger,
        WorkflowIntegrationEvent evt,
        IReadOnlyDictionary<Guid, ConnectedRepository> repositoryById,
        out ConnectedRepository repository)
    {
        repository = null!;
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
