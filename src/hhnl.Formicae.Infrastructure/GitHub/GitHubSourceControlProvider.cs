using System.Net;
using System.Text;
using hhnl.Formicae.Application.Workflows;
using Octokit;
using Workflow = hhnl.Formicae.Application.Workflows.Workflow;

namespace hhnl.Formicae.Infrastructure.GitHub;

public sealed class GitHubSourceControlProvider : ISourceControlProvider
{
    private readonly Func<string, CancellationToken, Task<IGitHubApi>> createApi;

    public GitHubSourceControlProvider(IGitHubClientFactory clientFactory)
        : this(async (repositoryUrl, cancellationToken) => new OctokitGitHubApi(await clientFactory.CreateClientForRepositoryAsync(repositoryUrl, cancellationToken)))
    {
    }

    public GitHubSourceControlProvider(GitHubClient client)
        : this(new OctokitGitHubApi(client))
    {
    }

    internal GitHubSourceControlProvider(IGitHubApi api)
        : this((_, _) => Task.FromResult(api))
    {
    }

    private GitHubSourceControlProvider(Func<string, CancellationToken, Task<IGitHubApi>> createApi)
    {
        this.createApi = createApi;
    }

    public async Task<string> CreateBranchAsync(CreateBranchRequest request, CancellationToken cancellationToken)
    {
        var repository = GitHubRepositoryReference.Parse(request.RepositoryUrl);
        var issue = GitHubIssueReference.Parse(request.LinkedWorkItemUrl);
        if (!string.Equals(repository.Owner, issue.Owner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(repository.Repository, issue.Repository, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Linked work item URL must belong to the workflow repository.", nameof(request));
        }

        var api = await createApi(request.RepositoryUrl, cancellationToken);
        var baseRef = await api.GetReferenceAsync(repository.Owner, repository.Repository, $"heads/{request.BaseBranch}");

        try
        {
            return await api.CreateLinkedBranchAsync(
                repository.Owner,
                repository.Repository,
                issue.Number,
                baseRef.Object.Sha,
                request.BranchName,
                cancellationToken);
        }
        catch (ApiException exception) when (IsAlreadyExists(exception))
        {
            return request.BranchName;
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return request.BranchName;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            try
            {
                await api.CreateReferenceAsync(repository.Owner, repository.Repository, request.BranchName, baseRef.Object.Sha);
            }
            catch (ApiException createReferenceException) when (IsAlreadyExists(createReferenceException))
            {
            }

            return request.BranchName;
        }
    }

    public async Task<PullRequestResult> CreatePullRequestAsync(Workflow workflow, IReadOnlyList<TaskRun> taskRuns, CancellationToken cancellationToken)
    {
        var repository = GitHubRepositoryReference.Parse(workflow.RepositoryUrl);
        var api = await createApi(workflow.RepositoryUrl, cancellationToken);
        var branchName = workflow.BranchName ?? throw new InvalidOperationException("Workflow branch is required before creating a pull request.");

        await UpsertWorkflowSummaryAsync(api, repository, workflow, taskRuns, branchName, cancellationToken);

        var existing = await api.ListPullRequestsAsync(repository.Owner, repository.Repository, repository.Owner, branchName);
        var pullRequest = existing.FirstOrDefault();
        if (pullRequest is not null)
        {
            return new PullRequestResult(pullRequest.HtmlUrl);
        }

        var title = await BuildPullRequestTitleAsync(api, repository, workflow, cancellationToken);
        var body = BuildPullRequestBody(workflow, taskRuns);
        var created = await api.CreatePullRequestAsync(repository.Owner, repository.Repository, title, branchName, workflow.BaseBranch, body);
        return new PullRequestResult(created.HtmlUrl);
    }

    public async Task<IReadOnlyList<PullRequestComment>> ListPullRequestCommentsAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var pullRequestUrl = workflow.PullRequestUrl ?? throw new InvalidOperationException("Workflow pull request URL is required before reading pull request comments.");
        var pullRequest = GitHubPullRequestReference.Parse(pullRequestUrl);
        var api = await createApi(pullRequest.RepositoryUrl, cancellationToken);

        var issueComments = await api.GetIssueCommentsAsync(pullRequest.Owner, pullRequest.Repository, pullRequest.Number);
        var reviewComments = await api.GetPullRequestReviewCommentsAsync(pullRequest.Owner, pullRequest.Repository, pullRequest.Number);

        return issueComments
            .Select(comment => new PullRequestComment(
                $"issue:{comment.Id}",
                comment.User?.Login ?? "unknown",
                comment.Body ?? string.Empty,
                comment.HtmlUrl,
                comment.UpdatedAt ?? comment.CreatedAt,
                PullRequestCommentKind.IssueComment))
            .Concat(reviewComments.Select(comment => new PullRequestComment(
                $"review:{comment.Id}",
                comment.User?.Login ?? "unknown",
                comment.Body ?? string.Empty,
                comment.HtmlUrl,
                comment.UpdatedAt,
                PullRequestCommentKind.ReviewComment)))
            .Where(comment => !string.IsNullOrWhiteSpace(comment.Body))
            .Where(comment => !PullRequestCommentMarkers.IsAutomationComment(comment.Body))
            .OrderBy(comment => comment.UpdatedAt)
            .ThenBy(comment => comment.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<PullRequestStatus> GetPullRequestStatusAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var pullRequestUrl = workflow.PullRequestUrl ?? throw new InvalidOperationException("Workflow pull request URL is required before reading pull request status.");
        var pullRequestReference = GitHubPullRequestReference.Parse(pullRequestUrl);
        var api = await createApi(pullRequestReference.RepositoryUrl, cancellationToken);
        var pullRequest = await api.GetPullRequestAsync(pullRequestReference.Owner, pullRequestReference.Repository, pullRequestReference.Number);

        return new PullRequestStatus(pullRequest.State.Value == ItemState.Open, pullRequest.Merged);
    }

    public async Task UpsertPullRequestCommentAsync(Workflow workflow, string body, CancellationToken cancellationToken)
    {
        var pullRequestUrl = workflow.PullRequestUrl ?? throw new InvalidOperationException("Workflow pull request URL is required before writing pull request comments.");
        var pullRequest = GitHubPullRequestReference.Parse(pullRequestUrl);
        var api = await createApi(pullRequest.RepositoryUrl, cancellationToken);

        await api.CreateIssueCommentAsync(pullRequest.Owner, pullRequest.Repository, pullRequest.Number, body);
    }

    public async Task ReactToPullRequestCommentAsync(Workflow workflow, PullRequestComment comment, string reaction, CancellationToken cancellationToken)
    {
        var pullRequestUrl = workflow.PullRequestUrl ?? throw new InvalidOperationException("Workflow pull request URL is required before reacting to pull request comments.");
        var pullRequest = GitHubPullRequestReference.Parse(pullRequestUrl);
        var api = await createApi(pullRequest.RepositoryUrl, cancellationToken);

        var (kind, id) = ParseCommentReference(comment.Id);
        if (kind == PullRequestCommentKind.IssueComment)
        {
            await api.ReactToIssueCommentAsync(pullRequest.Owner, pullRequest.Repository, id, reaction);
            return;
        }

        await api.ReactToPullRequestReviewCommentAsync(pullRequest.Owner, pullRequest.Repository, id, reaction);
    }

    private async Task UpsertWorkflowSummaryAsync(
        IGitHubApi api,
        GitHubRepositoryReference repository,
        Workflow workflow,
        IReadOnlyList<TaskRun> taskRuns,
        string branchName,
        CancellationToken cancellationToken)
    {
        var path = $".formicae/workflows/{workflow.Id:N}.md";
        var content = BuildWorkflowSummary(workflow, taskRuns);
        var message = $"Record Formicae workflow {workflow.Id:N}";
        RepositoryContent? existing = null;

        try
        {
            existing = (await api.GetContentsByRefAsync(repository.Owner, repository.Repository, path, branchName)).FirstOrDefault();
        }
        catch (NotFoundException)
        {
        }

        if (existing is null)
        {
            await api.CreateFileAsync(repository.Owner, repository.Repository, path, message, content, branchName);
            return;
        }

        await api.UpdateFileAsync(repository.Owner, repository.Repository, path, message, content, existing.Sha, branchName);
    }

    private async Task<string> BuildPullRequestTitleAsync(
        IGitHubApi api,
        GitHubRepositoryReference repository,
        Workflow workflow,
        CancellationToken cancellationToken)
    {
        var issue = GitHubIssueReference.Parse(workflow.IssueUrl);
        if (!string.Equals(repository.Owner, issue.Owner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(repository.Repository, issue.Repository, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Issue URL must belong to the workflow repository.", nameof(workflow));
        }

        var gitHubIssue = await api.GetIssueAsync(issue.Owner, issue.Repository, issue.Number);
        return string.IsNullOrWhiteSpace(gitHubIssue.Title)
            ? $"Issue #{issue.Number}"
            : gitHubIssue.Title.Trim();
    }

    private static string BuildPullRequestBody(Workflow workflow, IReadOnlyList<TaskRun> taskRuns)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Created by Formicae.");
        builder.AppendLine();
        builder.AppendLine($"Issue: {workflow.IssueUrl}");
        builder.AppendLine($"Workflow: `{workflow.Id:N}`");
        builder.AppendLine();
        builder.AppendLine("Task runs:");
        foreach (var run in taskRuns.OrderBy(run => run.CreatedAt))
        {
            builder.AppendLine($"- {run.Kind}: {run.Status}");
        }

        var implementation = taskRuns
            .OrderByDescending(run => run.UpdatedAt)
            .FirstOrDefault(run => run.Kind == TaskRunKind.Implement && !string.IsNullOrWhiteSpace(run.Output));
        if (implementation is not null)
        {
            builder.AppendLine();
            builder.AppendLine("## Implementation Summary");
            builder.AppendLine();
            builder.AppendLine(implementation.Output!.Trim());
        }

        return builder.ToString();
    }

    private static string BuildWorkflowSummary(Workflow workflow, IReadOnlyList<TaskRun> taskRuns)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Formicae Workflow");
        builder.AppendLine();
        builder.AppendLine($"- Workflow: `{workflow.Id:N}`");
        builder.AppendLine($"- Issue: {workflow.IssueUrl}");
        builder.AppendLine($"- Status: {workflow.Status}");
        builder.AppendLine();
        builder.AppendLine("## Task Runs");
        foreach (var run in taskRuns.OrderBy(run => run.CreatedAt))
        {
            builder.AppendLine();
            builder.AppendLine($"### {run.Kind}");
            builder.AppendLine();
            builder.AppendLine($"Status: {run.Status}");
            builder.AppendLine();
            builder.AppendLine("```text");
            builder.AppendLine(run.Output ?? string.Empty);
            builder.AppendLine("```");
        }

        return builder.ToString();
    }

    private static bool IsAlreadyExists(ApiException exception)
        => exception.StatusCode == HttpStatusCode.UnprocessableEntity
            || exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase);

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

internal sealed record GitHubRepositoryReference(string Owner, string Repository)
{
    public string RepositoryUrl => $"https://github.com/{Owner}/{Repository}";

    public static GitHubRepositoryReference Parse(string repositoryUrl)
    {
        var uri = new Uri(repositoryUrl);
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new ArgumentException("Expected a GitHub repository URL like https://github.com/{owner}/{repo}.", nameof(repositoryUrl));
        }

        return new GitHubRepositoryReference(parts[0], parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1]);
    }
}

internal sealed record GitHubPullRequestReference(string Owner, string Repository, int Number)
{
    public string RepositoryUrl => $"https://github.com/{Owner}/{Repository}";

    public static GitHubPullRequestReference Parse(string pullRequestUrl)
    {
        var uri = new Uri(pullRequestUrl);
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || !string.Equals(parts[2], "pull", StringComparison.OrdinalIgnoreCase) || !int.TryParse(parts[3], out var number))
        {
            throw new ArgumentException("Expected a GitHub pull request URL like https://github.com/{owner}/{repo}/pull/{number}.", nameof(pullRequestUrl));
        }

        return new GitHubPullRequestReference(parts[0], parts[1], number);
    }
}
