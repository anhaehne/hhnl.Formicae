using System.Text.Json;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Application.Integrations;

/// <summary>Provider-owned issue event implementation; each registration has a fixed action.</summary>
public sealed class IssueWorkflowEventDefinition : IWorkflowEventDefinition
{
    private readonly DevOpsProviderType? provider;
    private readonly string action;
    public WorkflowEventDescriptor Descriptor { get; }
    public IssueWorkflowEventDefinition(string uses, string title, DevOpsProviderType? provider, string action, bool legacy = false)
    {
        this.provider = provider;
        this.action = action;
        var fields = new List<WorkflowEventField> { new("repositoryIds", "Connected repositories", "repositories", true) };
        if (action == "labeled") fields.Add(new("label", "Label", "text", true));
        fields.Add(new("baseBranch", "Base Branch", "text"));
        fields.Add(new("model", "Workflow model", "text"));
        Descriptor = new(uses, title, $"Start when an issue is {(action == "opened" ? "created" : "given the selected label") }.", provider?.ToString(), false, false, fields, legacy, Outputs: uses == "github.issue-created" ? [new("issue", "string", true), new("issueId", "number", true), new("title", "string", true)] : null);
    }
    public WorkflowTriggerNodeSettings Compile(JsonElement configuration)
    {
        var settings = configuration.Deserialize<IssueEventSettings>(WorkflowEventDefinitions.JsonOptions)!;
        return new(action == "opened" ? WorkflowTriggerType.DevOpsIssueCreated : WorkflowTriggerType.DevOpsIssueLabel,
            settings.Enabled, settings.RepositoryIds ?? [], settings.Label, settings.BaseBranch, settings.Model);
    }
    public IReadOnlyList<string> Validate(JsonElement configuration)
    {
        var settings = Compile(configuration);
        if (!settings.Enabled) return [];
        var errors = new List<string>();
        if (settings.RepositoryIds.Count == 0) errors.Add("Enabled issue events require at least one connected repository.");
        if (action == "labeled" && string.IsNullOrWhiteSpace(settings.Label)) errors.Add("Enabled label events require a label.");
        if (action != "labeled" && !string.IsNullOrEmpty(settings.Label)) errors.Add("Issue created events do not have a label filter.");
        return errors;
    }
    public bool Matches(JsonElement configuration, WorkflowIntegrationEvent delivery)
    {
        var settings = Compile(configuration);
        return settings.Enabled && (provider is null || provider == delivery.ProviderType)
            && string.Equals(delivery.EventName, "issues", StringComparison.OrdinalIgnoreCase)
            && string.Equals(delivery.Action, action, StringComparison.OrdinalIgnoreCase)
            && (action != "labeled" || string.Equals(settings.Label, delivery.Label, StringComparison.OrdinalIgnoreCase));
    }
}

public static class GitHubWorkflowEvents
{
    public static void Register(WorkflowEventRegistry registry)
    {
        registry.Register(new IssueWorkflowEventDefinition("github.issue-created", "GitHub: Issue created", DevOpsProviderType.GitHub, "opened"));
        registry.Register(new IssueWorkflowEventDefinition("github.label-added", "GitHub: Label added", DevOpsProviderType.GitHub, "labeled"));
    }
}
public static class GiteaWorkflowEvents
{
    public static void Register(WorkflowEventRegistry registry)
        => registry.Register(new IssueWorkflowEventDefinition("gitea.label-added", "Gitea: Label added", DevOpsProviderType.Gitea, "labeled"));
}
