using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Application.Integrations;

public interface IDevOpsPlatform
{
    DevOpsProviderType ProviderType { get; }

    Task<DevOpsIssue> GetIssueAsync(DevOpsIssueReference issue, CancellationToken cancellationToken);

    Task<IReadOnlyList<DevOpsIssue>> ListIssuesWithLabelAsync(
        DevOpsRepositoryReference repository,
        string label,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DevOpsComment>> ListIssueCommentsAsync(DevOpsIssueReference issue, CancellationToken cancellationToken);

    Task CreateIssueCommentAsync(DevOpsIssueReference issue, string body, CancellationToken cancellationToken);

    Task UpdateIssueCommentAsync(DevOpsRepositoryReference repository, string commentId, string body, CancellationToken cancellationToken);

    Task ReactToIssueAsync(DevOpsIssueReference issue, string reaction, CancellationToken cancellationToken);

    Task ReactToIssueCommentAsync(DevOpsRepositoryReference repository, string commentId, string reaction, CancellationToken cancellationToken);

    Task<string> GetBranchHeadShaAsync(DevOpsRepositoryReference repository, string branchName, CancellationToken cancellationToken);

    Task<string> CreateBranchAsync(
        DevOpsRepositoryReference repository,
        DevOpsIssueReference? linkedIssue,
        string baseSha,
        string branchName,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DevOpsPullRequest>> ListPullRequestsAsync(
        DevOpsRepositoryReference repository,
        string headOwner,
        string headBranch,
        CancellationToken cancellationToken);

    Task<DevOpsPullRequest> GetPullRequestAsync(DevOpsPullRequestReference pullRequest, CancellationToken cancellationToken);

    Task<DevOpsPullRequest> CreatePullRequestAsync(
        DevOpsRepositoryReference repository,
        string title,
        string head,
        string baseBranch,
        string body,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DevOpsPullRequestComment>> ListPullRequestCommentsAsync(
        DevOpsPullRequestReference pullRequest,
        CancellationToken cancellationToken);

    Task CreatePullRequestCommentAsync(DevOpsPullRequestReference pullRequest, string body, CancellationToken cancellationToken);

    Task ReactToPullRequestCommentAsync(
        DevOpsPullRequestReference pullRequest,
        PullRequestComment comment,
        string reaction,
        CancellationToken cancellationToken);

    Task<DevOpsRepositoryFile?> GetFileAsync(
        DevOpsRepositoryReference repository,
        string path,
        string reference,
        CancellationToken cancellationToken);

    Task CreateFileAsync(
        DevOpsRepositoryReference repository,
        string path,
        string message,
        string content,
        string branch,
        CancellationToken cancellationToken);

    Task UpdateFileAsync(
        DevOpsRepositoryReference repository,
        string path,
        string message,
        string content,
        string sha,
        string branch,
        CancellationToken cancellationToken);
}

public interface IDevOpsPlatformFactory
{
    Task<DevOpsPlatformContext> CreateForRepositoryAsync(string repositoryUrl, CancellationToken cancellationToken);
}

public sealed record DevOpsPlatformContext(
    DevOpsIntegration Integration,
    ConnectedRepository ConnectedRepository,
    DevOpsRepositoryReference Repository,
    IDevOpsPlatform Platform);

public sealed record DevOpsIssue(
    string Url,
    string Title,
    string Body,
    bool IsPullRequest,
    IReadOnlyList<string> Labels);

public sealed record DevOpsComment(
    string Id,
    string Author,
    string Body,
    string Url,
    DateTimeOffset UpdatedAt);

public sealed record DevOpsPullRequest(
    string Url,
    string Title,
    bool IsOpen,
    bool IsMerged);

public sealed record DevOpsPullRequestComment(
    string Id,
    string Author,
    string Body,
    string Url,
    DateTimeOffset UpdatedAt,
    PullRequestCommentKind Kind);

public sealed record DevOpsRepositoryFile(string Path, string Sha);
