using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Infrastructure.GitHub;

public sealed class GitHubSourceControlProvider(HttpClient httpClient) : ISourceControlProvider
{
    public async Task<string> CreateBranchAsync(string repositoryUrl, string baseBranch, Guid workflowId, CancellationToken cancellationToken)
    {
        var repository = GitHubRepositoryReference.Parse(repositoryUrl);
        ConfigureClient();

        var branchName = $"formicae/{workflowId:N}";
        var baseRef = await httpClient.GetFromJsonAsync<GitHubRefDto>(
            $"repos/{repository.Owner}/{repository.Repository}/git/ref/heads/{Uri.EscapeDataString(baseBranch)}",
            cancellationToken)
            ?? throw new InvalidOperationException("GitHub base branch response was empty.");

        var response = await httpClient.PostAsJsonAsync(
            $"repos/{repository.Owner}/{repository.Repository}/git/refs",
            new CreateRefRequest($"refs/heads/{branchName}", baseRef.Object.Sha),
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            return branchName;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return branchName;
    }

    public async Task<PullRequestResult> CreatePullRequestAsync(Workflow workflow, IReadOnlyList<TaskRun> taskRuns, CancellationToken cancellationToken)
    {
        var repository = GitHubRepositoryReference.Parse(workflow.RepositoryUrl);
        var branchName = workflow.BranchName ?? throw new InvalidOperationException("Workflow branch is required before creating a pull request.");
        ConfigureClient();

        await UpsertWorkflowSummaryAsync(repository, workflow, taskRuns, branchName, cancellationToken);

        var existing = await httpClient.GetFromJsonAsync<IReadOnlyList<GitHubPullRequestDto>>(
            $"repos/{repository.Owner}/{repository.Repository}/pulls?state=open&head={Uri.EscapeDataString($"{repository.Owner}:{branchName}")}",
            cancellationToken) ?? [];

        var pullRequest = existing.FirstOrDefault();
        if (pullRequest is not null)
        {
            return new PullRequestResult(pullRequest.HtmlUrl);
        }

        var title = $"Formicae workflow for issue {workflow.IssueUrl}";
        var body = BuildPullRequestBody(workflow, taskRuns);
        var response = await httpClient.PostAsJsonAsync(
            $"repos/{repository.Owner}/{repository.Repository}/pulls",
            new CreatePullRequestRequest(title, branchName, workflow.BaseBranch, body, false),
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        var created = await response.Content.ReadFromJsonAsync<GitHubPullRequestDto>(cancellationToken)
            ?? throw new InvalidOperationException("GitHub pull request response was empty.");
        return new PullRequestResult(created.HtmlUrl);
    }

    public async Task<IReadOnlyList<PullRequestComment>> ListPullRequestCommentsAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var pullRequestUrl = workflow.PullRequestUrl ?? throw new InvalidOperationException("Workflow pull request URL is required before reading pull request comments.");
        var pullRequest = GitHubPullRequestReference.Parse(pullRequestUrl);
        ConfigureClient();

        var issueComments = await httpClient.GetFromJsonAsync<IReadOnlyList<GitHubIssueCommentDto>>(
            $"repos/{pullRequest.Owner}/{pullRequest.Repository}/issues/{pullRequest.Number}/comments",
            cancellationToken) ?? [];
        var reviewComments = await httpClient.GetFromJsonAsync<IReadOnlyList<GitHubReviewCommentDto>>(
            $"repos/{pullRequest.Owner}/{pullRequest.Repository}/pulls/{pullRequest.Number}/comments",
            cancellationToken) ?? [];

        return issueComments
            .Select(comment => new PullRequestComment(
                $"issue:{comment.Id}",
                comment.User?.Login ?? "unknown",
                comment.Body ?? string.Empty,
                comment.HtmlUrl,
                comment.UpdatedAt,
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
        ConfigureClient();

        var response = await httpClient.PostAsJsonAsync(
            $"repos/{pullRequest.Owner}/{pullRequest.Repository}/issues/{pullRequest.Number}/comments",
            new UpsertIssueCommentRequest(body),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task ReactToPullRequestCommentAsync(Workflow workflow, PullRequestComment comment, string reaction, CancellationToken cancellationToken)
    {
        var pullRequestUrl = workflow.PullRequestUrl ?? throw new InvalidOperationException("Workflow pull request URL is required before reacting to pull request comments.");
        var pullRequest = GitHubPullRequestReference.Parse(pullRequestUrl);
        ConfigureClient();

        var (kind, id) = ParseCommentReference(comment.Id);
        var endpoint = kind == PullRequestCommentKind.IssueComment
            ? $"repos/{pullRequest.Owner}/{pullRequest.Repository}/issues/comments/{id}/reactions"
            : $"repos/{pullRequest.Owner}/{pullRequest.Repository}/pulls/comments/{id}/reactions";
        var response = await httpClient.PostAsJsonAsync(endpoint, new CreateReactionRequest(reaction), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task UpsertWorkflowSummaryAsync(
        GitHubRepositoryReference repository,
        Workflow workflow,
        IReadOnlyList<TaskRun> taskRuns,
        string branchName,
        CancellationToken cancellationToken)
    {
        var path = $".formicae/workflows/{workflow.Id:N}.md";
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildWorkflowSummary(workflow, taskRuns)));
        string? sha = null;

        var existing = await httpClient.GetAsync(
            $"repos/{repository.Owner}/{repository.Repository}/contents/{path}?ref={Uri.EscapeDataString(branchName)}",
            cancellationToken);
        if (existing.IsSuccessStatusCode)
        {
            var file = await existing.Content.ReadFromJsonAsync<GitHubContentDto>(cancellationToken);
            sha = file?.Sha;
        }
        else if (existing.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(existing, cancellationToken);
        }

        var response = await httpClient.PutAsJsonAsync(
            $"repos/{repository.Owner}/{repository.Repository}/contents/{path}",
            new PutContentRequest(
                $"Record Formicae workflow {workflow.Id:N}",
                content,
                branchName,
                sha),
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
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

    private void ConfigureClient()
    {
        httpClient.BaseAddress ??= new Uri("https://api.github.com/");
        if (!httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hhnl-formicae", "0.1"));
        }

        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("GITHUB_TOKEN is required for GitHub source control operations.");
        }

        httpClient.DefaultRequestHeaders.Authorization ??= new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"GitHub API call failed with {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }

    private sealed record GitHubRefDto([property: JsonPropertyName("object")] GitHubRefObjectDto Object);
    private sealed record GitHubRefObjectDto([property: JsonPropertyName("sha")] string Sha);
    private sealed record CreateRefRequest([property: JsonPropertyName("ref")] string Ref, [property: JsonPropertyName("sha")] string Sha);
    private sealed record CreatePullRequestRequest(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("head")] string Head,
        [property: JsonPropertyName("base")] string Base,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("draft")] bool Draft);
    private sealed record GitHubPullRequestDto([property: JsonPropertyName("html_url")] string HtmlUrl);
    private sealed record GitHubUserDto([property: JsonPropertyName("login")] string Login);
    private sealed record GitHubIssueCommentDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
        [property: JsonPropertyName("user")] GitHubUserDto? User);
    private sealed record GitHubReviewCommentDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
        [property: JsonPropertyName("user")] GitHubUserDto? User);
    private sealed record GitHubContentDto([property: JsonPropertyName("sha")] string Sha);
    private sealed record UpsertIssueCommentRequest([property: JsonPropertyName("body")] string Body);
    private sealed record CreateReactionRequest([property: JsonPropertyName("content")] string Content);
    private sealed record PutContentRequest(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("branch")] string Branch,
        [property: JsonPropertyName("sha")] string? Sha);
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
