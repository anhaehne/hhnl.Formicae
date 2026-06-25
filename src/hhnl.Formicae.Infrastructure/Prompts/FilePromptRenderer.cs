using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Infrastructure.Prompts;

public sealed class FilePromptRenderer : IPromptRenderer
{
    public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? workItem, CancellationToken cancellationToken)
        => RenderAsync(kind, workflow, workItem, [], cancellationToken);

    public async Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? workItem, IReadOnlyList<PullRequestComment> pullRequestComments, CancellationToken cancellationToken)
    {
        var template = await LoadTemplateAsync(kind, cancellationToken);
        return template
            .Replace("{{workflow_id}}", workflow.Id.ToString("N"), StringComparison.Ordinal)
            .Replace("{{issue_url}}", workflow.IssueUrl, StringComparison.Ordinal)
            .Replace("{{repository_url}}", workflow.RepositoryUrl, StringComparison.Ordinal)
            .Replace("{{base_branch}}", workflow.BaseBranch, StringComparison.Ordinal)
            .Replace("{{branch_name}}", workflow.BranchName ?? $"formicae/{workflow.Id:N}", StringComparison.Ordinal)
            .Replace("{{plan_artifact}}", workflow.PlanArtifact ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{pull_request_url}}", workflow.PullRequestUrl ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{pull_request_comments}}", FormatPullRequestComments(pullRequestComments), StringComparison.Ordinal)
            .Replace("{{issue_title}}", workItem?.Title ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{issue_body}}", workItem?.Body ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{issue_comments}}", string.Join(Environment.NewLine, workItem?.Comments ?? []), StringComparison.Ordinal);
    }

    private static async Task<string> LoadTemplateAsync(TaskRunKind kind, CancellationToken cancellationToken)
    {
        var fileName = kind switch
        {
            TaskRunKind.Plan => "plan.md",
            TaskRunKind.Implement => "implement.md",
            TaskRunKind.CreatePullRequest => "pull-request.md",
            TaskRunKind.AddressComments => "address-comments.md",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        var path = Path.Combine(AppContext.BaseDirectory, "prompts", fileName);
        if (!File.Exists(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), "prompts", fileName);
        }

        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken)
            : DefaultTemplate(kind);
    }

    private static string FormatPullRequestComments(IReadOnlyList<PullRequestComment> comments)
        => string.Join(Environment.NewLine + Environment.NewLine, comments.Select(comment =>
            $"[{comment.Kind}] {comment.Author} at {comment.UpdatedAt:O}" + Environment.NewLine +
            $"URL: {comment.Url}" + Environment.NewLine +
            comment.Body));

    private static string DefaultTemplate(TaskRunKind kind)
        => kind switch
        {
            TaskRunKind.Plan => "Create an implementation plan for {{issue_url}} in {{repository_url}}.",
            TaskRunKind.Implement => "Implement this plan on {{branch_name}}:\n{{plan_artifact}}",
            TaskRunKind.CreatePullRequest => "Create a draft pull request for {{branch_name}}.",
            TaskRunKind.AddressComments => "Address these pull request comments on {{branch_name}}:\n{{pull_request_comments}}\n\nFull pull request conversation is mounted at /workspace/formicae/context/pull-request-conversation.md.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
}
