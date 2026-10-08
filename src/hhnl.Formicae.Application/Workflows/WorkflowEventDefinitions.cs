using System.Collections.Concurrent;
using System.Text.Json;
using hhnl.Formicae.Application.Integrations;

namespace hhnl.Formicae.Application.Workflows;

public sealed record WorkflowEventField(string Name, string Label, string Kind, bool Required = false);
public sealed record WorkflowEventDescriptor(string Uses, string Title, string Description, string? Provider,
    bool Manual, bool Webhook, IReadOnlyList<WorkflowEventField> Fields, bool Legacy = false);

/// <summary>Implemented by an integration to own its configuration, validation and delivery matching.</summary>
public interface IWorkflowEventDefinition
{
    WorkflowEventDescriptor Descriptor { get; }
    WorkflowTriggerNodeSettings Compile(JsonElement configuration);
    IReadOnlyList<string> Validate(JsonElement configuration);
    bool Matches(JsonElement configuration, WorkflowIntegrationEvent delivery);
}

/// <summary>Register definitions during application composition, before handling requests.</summary>
public sealed class WorkflowEventRegistry
{
    private readonly ConcurrentDictionary<string, IWorkflowEventDefinition> definitions = new(StringComparer.Ordinal);
    public static WorkflowEventRegistry Default { get; } = CreateDefault();
    public void Register(IWorkflowEventDefinition definition)
    {
        if (!definitions.TryAdd(definition.Descriptor.Uses, definition))
            throw new InvalidOperationException($"Event '{definition.Descriptor.Uses}' is already registered.");
    }
    public bool TryGet(string uses, out IWorkflowEventDefinition definition) => definitions.TryGetValue(uses, out definition!);
    public IReadOnlyList<WorkflowEventDescriptor> Catalog => definitions.Values.Select(item => item.Descriptor).OrderBy(item => item.Uses).ToArray();
    private static WorkflowEventRegistry CreateDefault()
    {
        var registry = new WorkflowEventRegistry();
        registry.Register(new ManualWorkflowEvent());
        registry.Register(new WebhookWorkflowEvent());
        GitHubWorkflowEvents.Register(registry);
        GiteaWorkflowEvents.Register(registry);
        // Old label nodes can select both providers. Keep that pinned behavior behind a compatibility definition.
        registry.Register(new IssueWorkflowEventDefinition("compat.issue-label", "Issue label added (legacy)", null, "labeled", true));
        return registry;
    }
}

public static class WorkflowEventDefinitions
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static bool IsEvent(string uses) => WorkflowEventRegistry.Default.TryGet(uses, out _);
    public static JsonElement Configuration(object settings) => JsonSerializer.SerializeToElement(settings, JsonOptions);
    public static WorkflowDefinitionDocument Adapt(WorkflowDefinitionDocument document) => document with
    {
        Steps = document.Steps.Select(node => node.Event is { } settings && WorkflowEventRegistry.Default.TryGet(node.Uses, out var definition)
            ? node with { Uses = WorkflowStartDefinitions.Uses, Trigger = definition.Compile(settings), Event = null } : node).ToArray()
    };
}

public sealed record ManualEventSettings(bool Enabled = true);
public sealed record WebhookEventSettings(bool Enabled = true, string? WebhookSecretName = null, string? BaseBranch = null, string? Model = null);
public sealed record IssueEventSettings(bool Enabled = true, IReadOnlyList<Guid>? RepositoryIds = null, string? Label = null, string? BaseBranch = null, string? Model = null);

public sealed class ManualWorkflowEvent : IWorkflowEventDefinition
{
    public WorkflowEventDescriptor Descriptor { get; } = new("builtins.start", "Start", "Start a workflow manually.", null, true, false, []);
    public WorkflowTriggerNodeSettings Compile(JsonElement configuration) => new(WorkflowTriggerType.Manual, configuration.Deserialize<ManualEventSettings>(WorkflowEventDefinitions.JsonOptions)!.Enabled, [], null);
    public IReadOnlyList<string> Validate(JsonElement configuration) { Compile(configuration); return []; }
    public bool Matches(JsonElement configuration, WorkflowIntegrationEvent delivery) => false;
}

public sealed class WebhookWorkflowEvent : IWorkflowEventDefinition
{
    public WorkflowEventDescriptor Descriptor { get; } = new("builtins.webhook", "Webhook", "Start from an authenticated webhook delivery.", null, false, true,
        [new("webhookSecretName", "Webhook secret name", "text", true), new("baseBranch", "Base Branch", "text"), new("model", "Workflow model", "text")]);
    public WorkflowTriggerNodeSettings Compile(JsonElement configuration)
    {
        var settings = configuration.Deserialize<WebhookEventSettings>(WorkflowEventDefinitions.JsonOptions)!;
        return new(WorkflowTriggerType.Webhook, settings.Enabled, [], null, settings.BaseBranch, settings.Model, settings.WebhookSecretName);
    }
    public IReadOnlyList<string> Validate(JsonElement configuration)
    {
        var settings = Compile(configuration);
        return settings.Enabled && !WorkflowExecutionExtensions.ValidName(settings.WebhookSecretName ?? "")
            ? ["Enabled webhook events require a valid operator-managed secret name."] : [];
    }
    public bool Matches(JsonElement configuration, WorkflowIntegrationEvent delivery) => false;
}
