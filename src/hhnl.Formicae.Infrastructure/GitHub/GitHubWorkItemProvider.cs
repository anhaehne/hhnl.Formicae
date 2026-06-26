using hhnl.Formicae.Application.Workflows;
using Octokit;

namespace hhnl.Formicae.Infrastructure.GitHub;

public sealed class GitHubWorkItemProvider : IWorkItemProvider
{
    private readonly Func<string, CancellationToken, Task<IGitHubApi>> createApi;

    public GitHubWorkItemProvider(IGitHubClientFactory clientFactory)
        : this(async (repositoryUrl, cancellationToken) => new OctokitGitHubApi(await clientFactory.CreateClientForRepositoryAsync(repositoryUrl, cancellationToken)))
    {
    }

    public GitHubWorkItemProvider(GitHubClient client)
        : this(new OctokitGitHubApi(client))
    {
    }

    internal GitHubWorkItemProvider(IGitHubApi api)
        : this((_, _) => Task.FromResult(api))
    {
    }

    private GitHubWorkItemProvider(Func<string, CancellationToken, Task<IGitHubApi>> createApi)
    {
        this.createApi = createApi;
    }

    public async Task<WorkItem> GetIssueAsync(string issueUrl, CancellationToken cancellationToken)
    {
        var issue = GitHubIssueReference.Parse(issueUrl);
        var api = await createApi(issue.RepositoryUrl, cancellationToken);

        var issueResponse = await api.GetIssueAsync(issue.Owner, issue.Repository, issue.Number);
        var comments = await api.GetIssueCommentsAsync(issue.Owner, issue.Repository, issue.Number);

        return ToWorkItem(issueUrl, issueResponse, comments);
    }

    public async Task<IReadOnlyList<WorkItem>> ListIssuesWithLabelAsync(
        string repositoryUrl,
        string label,
        CancellationToken cancellationToken)
    {
        var repository = GitHubRepositoryReference.Parse(repositoryUrl);
        var api = await createApi(repositoryUrl, cancellationToken);
        var issues = await api.ListIssuesWithLabelAsync(repository.Owner, repository.Repository, label);

        return issues
            .Where(issue => issue.PullRequest is null)
            .Select(issue => ToWorkItem(issue.HtmlUrl, issue, []))
            .ToArray();
    }

    public async Task UpsertIssueCommentAsync(
        string issueUrl,
        string marker,
        string body,
        CancellationToken cancellationToken)
    {
        var issue = GitHubIssueReference.Parse(issueUrl);
        var api = await createApi(issue.RepositoryUrl, cancellationToken);
        var comments = await api.GetIssueCommentsAsync(issue.Owner, issue.Repository, issue.Number);
        var existing = comments.FirstOrDefault(comment => (comment.Body ?? string.Empty).Contains(marker, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            await api.CreateIssueCommentAsync(issue.Owner, issue.Repository, issue.Number, body);
            return;
        }

        await api.UpdateIssueCommentAsync(issue.Owner, issue.Repository, existing.Id, body);
    }

    public async Task AddIssueCommentAsync(string issueUrl, string body, CancellationToken cancellationToken)
    {
        var issue = GitHubIssueReference.Parse(issueUrl);
        var api = await createApi(issue.RepositoryUrl, cancellationToken);
        await api.CreateIssueCommentAsync(issue.Owner, issue.Repository, issue.Number, body);
    }

    public async Task ReactToIssueAsync(string issueUrl, string reaction, CancellationToken cancellationToken)
    {
        var issue = GitHubIssueReference.Parse(issueUrl);
        var api = await createApi(issue.RepositoryUrl, cancellationToken);
        await api.ReactToIssueAsync(issue.Owner, issue.Repository, issue.Number, reaction);
    }

    private static WorkItem ToWorkItem(string issueUrl, Issue issue, IReadOnlyList<IssueComment> comments)
        => new(
            issueUrl,
            issue.Title,
            issue.Body ?? string.Empty,
            comments
                .Where(comment => !PullRequestCommentMarkers.IsAutomationComment(comment.Body ?? string.Empty))
                .Select(comment => new WorkItemComment(
                    comment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    comment.User?.Login ?? "unknown",
                    comment.Body ?? string.Empty,
                    comment.HtmlUrl ?? issueUrl,
                    comment.UpdatedAt ?? DateTimeOffset.MinValue))
                .ToArray(),
            (issue.Labels ?? []).Select(label => label.Name).ToArray());
}

internal sealed record GitHubIssueReference(string Owner, string Repository, int Number)
{
    public string RepositoryUrl => $"https://github.com/{Owner}/{Repository}";

    public static GitHubIssueReference Parse(string issueUrl)
    {
        var uri = new Uri(issueUrl);
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || !string.Equals(parts[2], "issues", StringComparison.OrdinalIgnoreCase) || !int.TryParse(parts[3], out var number))
        {
            throw new ArgumentException("Expected a GitHub issue URL like https://github.com/{owner}/{repo}/issues/{number}.", nameof(issueUrl));
        }

        return new GitHubIssueReference(parts[0], parts[1], number);
    }
}