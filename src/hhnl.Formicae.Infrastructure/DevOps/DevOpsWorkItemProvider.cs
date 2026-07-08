using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Infrastructure.DevOps;

public sealed class DevOpsWorkItemProvider(IDevOpsPlatformFactory platformFactory) : IWorkItemProvider
{
    public async Task<WorkItem> GetIssueAsync(string issueUrl, CancellationToken cancellationToken)
    {
        var context = await CreateContextForIssueAsync(issueUrl, cancellationToken);
        var issue = await context.Platform.GetIssueAsync(
            DevOpsReferenceParser.ParseIssueUrl(context.Integration.ProviderType, issueUrl, context.Integration.ServerUrl),
            cancellationToken);
        var comments = await context.Platform.ListIssueCommentsAsync(
            DevOpsReferenceParser.ParseIssueUrl(context.Integration.ProviderType, issueUrl, context.Integration.ServerUrl),
            cancellationToken);

        return ToWorkItem(issue.Url, issue, comments);
    }

    public async Task<IReadOnlyList<WorkItem>> ListIssuesWithLabelAsync(
        string repositoryUrl,
        string label,
        CancellationToken cancellationToken)
    {
        var context = await platformFactory.CreateForRepositoryAsync(repositoryUrl, cancellationToken);
        var issues = await context.Platform.ListIssuesWithLabelAsync(context.Repository, label, cancellationToken);
        return issues
            .Where(issue => !issue.IsPullRequest)
            .Select(issue => ToWorkItem(issue.Url, issue, []))
            .ToArray();
    }

    public async Task UpsertIssueCommentAsync(string issueUrl, string marker, string body, CancellationToken cancellationToken)
    {
        var context = await CreateContextForIssueAsync(issueUrl, cancellationToken);
        var issue = DevOpsReferenceParser.ParseIssueUrl(context.Integration.ProviderType, issueUrl, context.Integration.ServerUrl);
        var comments = await context.Platform.ListIssueCommentsAsync(issue, cancellationToken);
        var existing = comments.FirstOrDefault(comment => comment.Body.Contains(marker, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            await context.Platform.CreateIssueCommentAsync(issue, body, cancellationToken);
            return;
        }

        await context.Platform.UpdateIssueCommentAsync(context.Repository, existing.Id, body, cancellationToken);
    }

    public async Task AddIssueCommentAsync(string issueUrl, string body, CancellationToken cancellationToken)
    {
        var context = await CreateContextForIssueAsync(issueUrl, cancellationToken);
        var issue = DevOpsReferenceParser.ParseIssueUrl(context.Integration.ProviderType, issueUrl, context.Integration.ServerUrl);
        await context.Platform.CreateIssueCommentAsync(issue, body, cancellationToken);
    }

    public async Task ReactToIssueAsync(string issueUrl, string reaction, CancellationToken cancellationToken)
    {
        var context = await CreateContextForIssueAsync(issueUrl, cancellationToken);
        var issue = DevOpsReferenceParser.ParseIssueUrl(context.Integration.ProviderType, issueUrl, context.Integration.ServerUrl);
        await context.Platform.ReactToIssueAsync(issue, reaction, cancellationToken);
    }

    public async Task ReactToIssueCommentAsync(string issueUrl, WorkItemComment comment, string reaction, CancellationToken cancellationToken)
    {
        var context = await CreateContextForIssueAsync(issueUrl, cancellationToken);
        await context.Platform.ReactToIssueCommentAsync(context.Repository, comment.Id, reaction, cancellationToken);
    }

    private async Task<DevOpsPlatformContext> CreateContextForIssueAsync(string issueUrl, CancellationToken cancellationToken)
    {
        foreach (var providerType in new[] { DevOpsProviderType.GitHub, DevOpsProviderType.Gitea })
        {
            try
            {
                var issue = DevOpsReferenceParser.ParseIssueUrl(providerType, issueUrl, providerType == DevOpsProviderType.GitHub ? null : new Uri(issueUrl).GetLeftPart(UriPartial.Authority));
                return await platformFactory.CreateForRepositoryAsync(issue.RepositoryUrl, cancellationToken);
            }
            catch (ArgumentException)
            {
            }
        }

        throw new ArgumentException("Issue URL does not match a connected DevOps provider.", nameof(issueUrl));
    }

    private static WorkItem ToWorkItem(string issueUrl, DevOpsIssue issue, IReadOnlyList<DevOpsComment> comments)
        => new(
            issueUrl,
            issue.Title,
            issue.Body,
            comments
                .Where(comment => !PullRequestCommentMarkers.IsAutomationComment(comment.Body))
                .Select(comment => new WorkItemComment(comment.Id, comment.Author, comment.Body, comment.Url, comment.UpdatedAt))
                .ToArray(),
            issue.Labels);
}
