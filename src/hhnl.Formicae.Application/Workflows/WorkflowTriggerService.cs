using hhnl.Formicae.Application.Integrations;
namespace hhnl.Formicae.Application.Workflows;

// Compatibility facade for existing callers. New integrations use WorkflowEventService.
public sealed class WorkflowTriggerService(IWorkflowStore store, IDevOpsIntegrationStore integrations, WorkflowService workflows, IClock? clock = null)
{
    private readonly WorkflowEventService events = new(store, integrations, workflows, clock);
    public Task<IReadOnlyList<Guid>> HandleIssueLabelEventAsync(DevOpsIssueLabelTriggerEvent delivery, CancellationToken token)
        => events.HandleIssueEventAsync(delivery, token);
}
