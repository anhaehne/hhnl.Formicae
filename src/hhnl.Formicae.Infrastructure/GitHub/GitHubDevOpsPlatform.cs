using System.Globalization;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using Octokit;

namespace hhnl.Formicae.Infrastructure.GitHub;

public sealed class GitHubDevOpsPlatform(IGitHubClientFactory clientFactory) : IDevOpsPlatform
{
    public DevOpsProviderType ProviderType => DevOpsProviderType.GitHub;

    public async Task<DevOpsIssue> GetIssueAsync(DevOpsIssueReference issue, CancellationToken cancellationToken)
    {
        var api = await CreateApiAsync(issue.RepositoryUrl, cancellationToken);
        return ToIssue(await api.GetIssueAsync(issue.Owner, issue.Repository, issue.Number));
    }

    public async Task<IReadOnlyList<DevOpsIssue>> ListIssuesWithLabelAsync(DevOpsRepositoryReference repository, string label, CancellationToken cancellationToken)
    {
        var api = await CreateApiAsync(repository.RepositoryUrl, cancellationToken);
        return (await api.ListIssuesWithLabelAsync(repository.Owner, repository.Name, label)).Select(ToIssue).ToArray();
    }

    public async Task<IReadOnlyList<DevOpsComment>> ListIssueCommentsAsync(DevOpsIssueReference issue, CancellationToken cancellationToken)
    {
        var api = await CreateApiAsync(issue.RepositoryUrl, cancellationToken);
        return (await api.GetIssueCommentsAsync(issue.Owner, issue.Repository, issue.Number)).Select(ToComment).ToArray();
    }

    public async Task CreateIssueCommentAsync(DevOpsIssueReference issue, string body, CancellationToken cancellationToken)
    {
        var api = await CreateApiAsync(issue.RepositoryUrl, cancellationToken);
        await api.CreateIssueCommentAsync(issue.Owner, issue.Repository, issue.Number, body);
    }

    public async Task UpdateIssueCommentAsync(DevOpsRepositoryReference repository, string commentId, string body, CancellationToken cancellationToken)
    {
        var api = await CreateApiAsync(repository.RepositoryUrl, cancellationToken);
        await api.UpdateIssueCommentAsync(repository.Owner, repository.Name, ParseLong(commentId), body);
    }

    public async Task ReactToIssueAsync(DevOpsIssueReference issue, string reaction, CancellationToken cancellationToken)
    {
        var api = await CreateApiAsync(issue.RepositoryUrl, cancellationToken);
        await api.ReactToIssueAsync(issue.Owner, issue.Repository, issue.Number, reaction);
    }

    public async Task ReactToIssueCommentAsync(DevOpsRepositoryReference repository, string commentId, string reaction, CancellationToken cancellationToken)
    {
        var api = await CreateApiAsync(repository.RepositoryUrl, cancellationToken);
        await api.ReactToIssueCommentAsync(repository.Owner, repository.Name, ParseLong(commentId), reaction);
    }

    public async Task<string> GetBranchHeadShaAsync(DevOpsRepositoryReference repository, string branchName, CancellationToken cancellationToken)
    {
        var api = await CreateApiAsync(repository.RepositoryUrl, cancellationToken);
        var reference = await api.GetReferenceAsync(repository.Owner, repository.Name, $"heads/{branchName}");
        return reference.Object.Sha;
    }

    public async Task<string> CreateBranchAsync(DevOpsRepositoryReference repository, DevOpsIssueReference? linkedIssue, string baseSha, string branchName, CancellationToken cancellationToken)
    {
        var api = await CreateApiAsync(repository.RepositoryUrl, cancellationToken);
        if (linkedIssue is not null)
        {
            return await api.CreateLinkedBranchAsync(repository.Owner, repository.Name, linkedIssue.Number, baseSha, branchName, cancellationToken);
        }

        await api.CreateReferenceAsync(repository.Owner, repository.Name, $"refs/heads/{branchName}", baseSha);
        return branchName;
    }

    public async Task<IReadOnlyList<DevOpsPullRequest>> ListPullRequestsAsync(DevOpsRepositoryReference repository, string headOwner, string headBranch, CancellationToken cancellationToken)
    {
        var api = await CreateApiAsync(repository.RepositoryUrl, cancellationToken);
        return (await api.ListPullRequestsAsync(repository.Owner, repository.Name, headOwner, headBranch)).Select(ToPullRequest).ToArray();
    }

    public async Task<DevOpsPullRequest> GetPullRequestAsync(DevOpsPullRequestReference pullRequest, CancellationToken cancellationToken)
    {
        var api = await CreateApiAsync(pullRequest.RepositoryUrl, cancellationToken);
        return ToPullRequest(await api.GetPullRequestAsync(pullRequest.Owner, pullRequest.Repository, pullRequest.Number));
    }

    public async Task<DevOpsPullRequest> CreatePullRequestAsync(DevOpsRepositoryReference repository, string title, string head, string baseBranch, string body, CancellationToken cancellationToken)
    {
        var api = await CreateApiAsync(repository.RepositoryUrl, cancellationToken);
        return ToPullRequest(await api.CreatePullRequestAsync(repository.Owner, repository.Name, title, head, baseBranch, body));
    }

    public async Task<IReadOnlyList<DevOpsPullRequestComment>> ListPullRequestCommentsAsync(DevOpsPullRequestReference pullRequest, CancellationToken cancellationToken)
    {
        var api = await CreateApiAsync(pullRequest.RepositoryUrl, cancellationToken);
        var issueComments = await api.GetIssueCommentsAsync(pullRequest.Owner, pullRequest.Repository, pullRequest.Number);
        var reviewComments = await api.GetPullRequestReviewCommentsAsync(pullRequest.Owner, pullRequest.Repository, pullRequest.Number);

        return issueComments
            .Select(comment => new DevOpsPullRequestComment(
                $"issue:{comment.Id}",
                comment.User?.Login ?? "unknown",
                comment.Body ?? string.Empty,
                comment.HtmlUrl,
                comment.UpdatedAt ?? comment.CreatedAt,
                PullRequestCommentKind.IssueComment))
            .Concat(reviewComments.Select(comment => new DevOpsPullRequestComment(
                $"review:{comment.Id}",
                comment.User?.Login ?? "unknown",
                comment.Body ?? string.Empty,
                comment.HtmlUrl,
                comment.UpdatedAt,
                PullRequestCommentKind.ReviewComment)))
            .ToArray();
    }

    public async Task CreatePullRequestCommentAsync(DevOpsPullRequestReference pullRequest, string body, CancellationToken cancellationToken)
    {
        var api = await CreateApiAsync(pullRequest.RepositoryUrl, cancellationToken);
        await api.CreateIssueCommentAsync(pullRequest.Owner, pullRequest.Repository, pullRequest.Number, body);
    }

    public async Task ReactToPullRequestCommentAsync(DevOpsPullRequestReference pullRequest, PullRequestComment comment, string reaction, CancellationToken cancellationToken)
    {
        var api = await CreateApiAsync(pullRequest.RepositoryUrl, cancellationToken);
        var (kind, id) = ParseCommentReference(comment.Id);
        if (kind == PullRequestCommentKind.IssueComment)
        {
            await api.ReactToIssueCommentAsync(pullRequest.Owner, pullRequest.Repository, id, reaction);
            return;
        }

        await api.ReactToPullRequestReviewCommentAsync(pullRequest.Owner, pullRequest.Repository, id, reaction);
    }

    public async Task<DevOpsRepositoryFile?> GetFileAsync(DevOpsRepositoryReference repository, string path, string reference, CancellationToken cancellationToken)
    {
        var api = await CreateApiAsync(repository.RepositoryUrl, cancellationToken);
        try
        {
            var file = (await api.GetContentsByRefAsync(repository.Owner, repository.Name, path, reference)).FirstOrDefault();
            return file is null ? null : new DevOpsRepositoryFile(path, file.Sha);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    public async Task CreateFileAsync(DevOpsRepositoryReference repository, string path, string message, string content, string branch, CancellationToken cancellationToken)
    {
        var api = await CreateApiAsync(repository.RepositoryUrl, cancellationToken);
        await api.CreateFileAsync(repository.Owner, repository.Name, path, message, content, branch);
    }

    public async Task UpdateFileAsync(DevOpsRepositoryReference repository, string path, string message, string content, string sha, string branch, CancellationToken cancellationToken)
    {
        var api = await CreateApiAsync(repository.RepositoryUrl, cancellationToken);
        await api.UpdateFileAsync(repository.Owner, repository.Name, path, message, content, sha, branch);
    }

    private async Task<IGitHubApi> CreateApiAsync(string repositoryUrl, CancellationToken cancellationToken)
        => new OctokitGitHubApi(await clientFactory.CreateClientForRepositoryAsync(repositoryUrl, cancellationToken));

    private static DevOpsIssue ToIssue(Issue issue)
        => new(issue.HtmlUrl, issue.Title, issue.Body ?? string.Empty, issue.PullRequest is not null, (issue.Labels ?? []).Select(label => label.Name).ToArray());

    private static DevOpsComment ToComment(IssueComment comment)
        => new(
            comment.Id.ToString(CultureInfo.InvariantCulture),
            comment.User?.Login ?? "unknown",
            comment.Body ?? string.Empty,
            comment.HtmlUrl,
            comment.UpdatedAt ?? DateTimeOffset.MinValue);

    private static DevOpsPullRequest ToPullRequest(PullRequest pullRequest)
        => new(pullRequest.HtmlUrl, pullRequest.Title, pullRequest.State.Value == ItemState.Open, pullRequest.Merged);

    private static long ParseLong(string value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Expected a numeric GitHub id, but received '{value}'.", nameof(value));

    private static (PullRequestCommentKind Kind, long Id) ParseCommentReference(string commentId)
    {
        var parts = commentId.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !long.TryParse(parts[1], out var id))
        {
            throw new ArgumentException($"Unsupported pull request comment id '{commentId}'.", nameof(commentId));
        }

        return parts[0].Equals("issue", StringComparison.OrdinalIgnoreCase)
            ? (PullRequestCommentKind.IssueComment, id)
            : parts[0].Equals("review", StringComparison.OrdinalIgnoreCase)
                ? (PullRequestCommentKind.ReviewComment, id)
                : throw new ArgumentException($"Unsupported pull request comment id '{commentId}'.", nameof(commentId));
    }
}
