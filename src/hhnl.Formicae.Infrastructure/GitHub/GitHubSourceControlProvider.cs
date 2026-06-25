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

    public async Task<PullRequestResult> CreateDraftPullRequestAsync(Workflow workflow, IReadOnlyList<TaskRun> taskRuns, CancellationToken cancellationToken)
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
            new CreatePullRequestRequest(title, branchName, workflow.BaseBranch, body, true),
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        var created = await response.Content.ReadFromJsonAsync<GitHubPullRequestDto>(cancellationToken)
            ?? throw new InvalidOperationException("GitHub pull request response was empty.");
        return new PullRequestResult(created.HtmlUrl);
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
    private sealed record GitHubContentDto([property: JsonPropertyName("sha")] string Sha);
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
