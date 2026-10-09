using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Application.Integrations;

public sealed class GitHubIssueCommentWaitDefinition : IWorkflowWaitDefinition
{
    public const string Uses = "github.issue-commented";
    public WorkflowEventDescriptor Descriptor { get; } = new(Uses, "GitHub: Issue commented",
        "Wait for a new issue comment, then continue this execution.", "GitHub", false, false,
        [new("repositoryId", "Connected repository", "repository"), new("issueNumber", "Issue number", "number", true)], Callable: true);
    public IReadOnlyList<CustomTaskOutputDefinition> Outputs { get; } = [
        new("commentId", "string", true), new("body", "string", true), new("author", "string", true),
        new("url", "string", true), new("createdAt", "string", true)];
    public IReadOnlyList<string> Validate(WorkflowWaitSettings? settings)
    {
        if (settings is null) return ["Issue commented requires wait settings."];
        if (settings.IssueNumberBinding is { } binding)
            return settings.IssueNumber is not null || string.IsNullOrWhiteSpace(binding.StepId) || string.IsNullOrWhiteSpace(binding.OutputName)
                ? ["Select either a positive issue number or a valid issue-number binding."] : [];
        return settings.IssueNumber is > 0 ? [] : ["A positive issue number is required."];
    }
    public async Task<WorkflowWaitCorrelation> ResolveAsync(WorkflowWaitSettings settings, Workflow workflow, int issueNumber,
        IDevOpsIntegrationStore integrations, IWorkItemProvider workItems, CancellationToken token)
    {
        var repositories = await integrations.ListAllRepositoriesAsync(token);
        var repository = settings.RepositoryId is { } id ? repositories.SingleOrDefault(item => item.Id == id)
            : repositories.FirstOrDefault(item => string.Equals(item.RepositoryUrl, workflow.RepositoryUrl, StringComparison.OrdinalIgnoreCase));
        var integration = repository is null ? null : await integrations.GetAsync(repository.DevOpsIntegrationId, token);
        if (integration?.ProviderType != DevOpsProviderType.GitHub)
            throw new InvalidOperationException("Issue commented requires a connected GitHub repository.");
        var repositoryUrl = repository!.RepositoryUrl.TrimEnd('/').ToLowerInvariant();
        var issueUrl = $"{repositoryUrl}/issues/{issueNumber}";
        var issue = await workItems.GetIssueAsync(issueUrl, token); // Existing provider adapter checks issue access.
        var watermark = issue.Comments.Select(comment => long.TryParse(comment.Id, out var id) ? id : 0).DefaultIfEmpty().Max();
        return new("GitHub", repositoryUrl, issueUrl, watermark);
    }
}
