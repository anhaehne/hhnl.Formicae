using System.Text.Json;
using hhnl.Formicae.Application.Integrations;

namespace hhnl.Formicae.Application.Workflows;

public sealed record WorkflowWaitSettings(Guid? RepositoryId = null, int? IssueNumber = null, CustomTaskInputBinding? IssueNumberBinding = null);
public sealed record WorkflowWaitCorrelation(string Provider, string RepositoryUrl, string IssueUrl, long EventSequenceFloor = 0);

// Integration-owned validation and correlation, independent of workflow entry events.
public interface IWorkflowWaitDefinition
{
    WorkflowEventDescriptor Descriptor { get; }
    IReadOnlyList<CustomTaskOutputDefinition> Outputs { get; }
    IReadOnlyList<string> Validate(WorkflowWaitSettings? settings);
    Task<WorkflowWaitCorrelation> ResolveAsync(WorkflowWaitSettings settings, Workflow workflow, int issueNumber,
        IDevOpsIntegrationStore integrations, IWorkItemProvider workItems, CancellationToken token);
}

public sealed class WorkflowWaitRegistry
{
    private readonly Dictionary<string, IWorkflowWaitDefinition> definitions = new(StringComparer.Ordinal);
    public static WorkflowWaitRegistry Default { get; } = CreateDefault();
    public void Register(IWorkflowWaitDefinition definition) => definitions.Add(definition.Descriptor.Uses, definition);
    public bool TryGet(string uses, out IWorkflowWaitDefinition definition) => definitions.TryGetValue(uses, out definition!);
    public IReadOnlyList<WorkflowEventDescriptor> Catalog => definitions.Values.Select(item => item.Descriptor).ToArray();
    private static WorkflowWaitRegistry CreateDefault()
    {
        var registry = new WorkflowWaitRegistry();
        registry.Register(new GitHubIssueCommentWaitDefinition());
        return registry;
    }
}

public sealed class WorkflowNodeWait
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid WorkflowId { get; init; }
    public Guid TaskRunId { get; init; }
    public Guid ExecutionAttemptId { get; init; }
    public required string Uses { get; init; }
    public required string Provider { get; init; }
    public required string RepositoryUrl { get; init; }
    public required string IssueUrl { get; init; }
    public long EventSequenceFloor { get; set; }
    public DateTimeOffset ArmedAt { get; init; }
    public string? InputProvenanceJson { get; init; }
    public Guid? MatchedEventId { get; set; }
    public DateTimeOffset? MatchedAt { get; set; }
    public bool IsCanceled { get; set; }
}

// Durable inbox: creation time prevents a late delivery or an unconsumed comment from waking a later activation.
public sealed class WorkflowWaitEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Provider { get; init; }
    public required string DeliveryId { get; init; }
    public long EventSequence { get; init; }
    public required string EventKey { get; init; }
    public required string Uses { get; init; }
    public required string RepositoryUrl { get; init; }
    public required string IssueUrl { get; init; }
    public required string OutputsJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
}
