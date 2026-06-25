using System.Net;
using System.Text;
using hhnl.Formicae.Application.Workflows;
using Octokit;
using Workflow = hhnl.Formicae.Application.Workflows.Workflow;

namespace hhnl.Formicae.Infrastructure.GitHub;

public sealed class GitHubSourceControlProvider : ISourceControlProvider
{
    private readonly IGitHubApi api;

    public GitHubSourceControlProvider(GitHubClient client)
        : this(new OctokitGitHubApi(client))
    {
    }

    internal GitHubSourceControlProvider(IGitHubApi api)
    {
        this.api = api;
    }

    public async Task<string> CreateBranchAsync(string repositoryUrl, string baseBranch, string issueUrl, Guid workflowId, CancellationToken cancellationToken)
    {
        var repository = GitHubRepositoryReference.Parse(repositoryUrl);
        var issue = GitHubIssueReference.Parse(issueUrl);
        if (!string.Equals(repository.Owner, issue.Owner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(repository.Repository, issue.Repository, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Issue URL must belong to the workflow repository.", nameof(issueUrl));
        }

        var branchName = $"formicae/{workflowId:N}";
        var baseRef = await api.GetReferenceAsync(repository.Owner, repository.Repository, $"heads/{baseBranch}");

        try
        {
            return await api.CreateLinkedBranchAsync(
                repository.Owner,
                repository.Repository,
                issue.Number,
                baseRef.Object.Sha,
                branchName,
                cancellationToken);
        }
        catch (ApiException exception) when (exception.StatusCode == HttpStatusCode.UnprocessableEntity
            || exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return branchName;
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return branchName;
        }
    }

    public async Task<PullRequestResult> CreatePullRequestAsync(Workflow workflow, IReadOnlyList<TaskRun> taskRuns, CancellationToken cancellationToken)
    {
        var repository = GitHubRepositoryReference.Parse(workflow.RepositoryUrl);
        var branchName = workflow.BranchName ?? throw new InvalidOperationException("Workflow branch is required before creating a pull request.");

        await UpsertWorkflowSummaryAsync(repository, workflow, taskRuns, branchName, cancellationToken);

        var existing = await api.ListPullRequestsAsync(repository.Owner, repository.Repository, repository.Owner, branchName);
        var pullRequest = existing.FirstOrDefault();
        if (pullRequest is not null)
        {
            return new PullRequestResult(pullRequest.HtmlUrl);
        }

        var title = $"Formicae workflow for issue {workflow.IssueUrl}";
        var body = BuildPullRequestBody(workflow, taskRuns);
        var created = await api.CreatePullRequestAsync(repository.Owner, repository.Repository, title, branchName, workflow.BaseBranch, body);
        return new PullRequestResult(created.HtmlUrl);
    }

    public async Task<IReadOnlyList<PullRequestComment>> ListPullRequestCommentsAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var pullRequestUrl = workflow.PullRequestUrl ?? throw new InvalidOperationException("Workflow pull request URL is required before reading pull request comments.");
        var pullRequest = GitHubPullRequestReference.Parse(pullRequestUrl);

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

    public async Task UpsertPullRequestCommentAsync(Workflow workflow, string body, CancellationToken cancellationToken)
    {
        var pullRequestUrl = workflow.PullRequestUrl ?? throw new InvalidOperationException("Workflow pull request URL is required before writing pull request comments.");
        var pullRequest = GitHubPullRequestReference.Parse(pullRequestUrl);

        await api.CreateIssueCommentAsync(pullRequest.Owner, pullRequest.Repository, pullRequest.Number, body);
    }

    public async Task ReactToPullRequestCommentAsync(Workflow workflow, PullRequestComment comment, string reaction, CancellationToken cancellationToken)
    {
        var pullRequestUrl = workflow.PullRequestUrl ?? throw new InvalidOperationException("Workflow pull request URL is required before reacting to pull request comments.");
        var pullRequest = GitHubPullRequestReference.Parse(pullRequestUrl);

        var (kind, id) = ParseCommentReference(comment.Id);
        if (kind == PullRequestCommentKind.IssueComment)
        {
            await api.ReactToIssueCommentAsync(pullRequest.Owner, pullRequest.Repository, id, reaction);
            return;
        }

        await api.ReactToPullRequestReviewCommentAsync(pullRequest.Owner, pullRequest.Repository, id, reaction);
    }

    private async Task UpsertWorkflowSummaryAsync(
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
