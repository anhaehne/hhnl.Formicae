using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Infrastructure.GitHub;

public sealed class GitHubWorkItemProvider(HttpClient httpClient) : IWorkItemProvider
{
    public async Task<WorkItem> GetIssueAsync(string issueUrl, CancellationToken cancellationToken)
    {
        var issue = GitHubIssueReference.Parse(issueUrl);
        ConfigureClient();

        var issueResponse = await httpClient.GetFromJsonAsync<GitHubIssueDto>($"repos/{issue.Owner}/{issue.Repository}/issues/{issue.Number}", cancellationToken)
            ?? throw new InvalidOperationException("GitHub issue response was empty.");
        var comments = await httpClient.GetFromJsonAsync<IReadOnlyList<GitHubCommentDto>>($"repos/{issue.Owner}/{issue.Repository}/issues/{issue.Number}/comments", cancellationToken) ?? [];

        return ToWorkItem(issueUrl, issueResponse, comments);
    }

    public async Task<IReadOnlyList<WorkItem>> ListIssuesWithLabelAsync(
        string repositoryUrl,
        string label,
        CancellationToken cancellationToken)
    {
        var repository = GitHubRepositoryReference.Parse(repositoryUrl);
        ConfigureClient();

        var requestUri = $"repos/{repository.Owner}/{repository.Repository}/issues?state=open&labels={Uri.EscapeDataString(label)}&per_page=100";
        var issues = await httpClient.GetFromJsonAsync<IReadOnlyList<GitHubIssueDto>>(requestUri, cancellationToken) ?? [];

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
        ConfigureClient();

        var comments = await httpClient.GetFromJsonAsync<IReadOnlyList<GitHubCommentDto>>(
            $"repos/{issue.Owner}/{issue.Repository}/issues/{issue.Number}/comments",
            cancellationToken) ?? [];
        var existing = comments.FirstOrDefault(comment => (comment.Body ?? string.Empty).Contains(marker, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            await httpClient.PostAsJsonAsync(
                $"repos/{issue.Owner}/{issue.Repository}/issues/{issue.Number}/comments",
                new UpsertIssueCommentRequest(body),
                cancellationToken);
            return;
        }

        await httpClient.PatchAsJsonAsync(
            $"repos/{issue.Owner}/{issue.Repository}/issues/comments/{existing.Id}",
            new UpsertIssueCommentRequest(body),
            cancellationToken);
    }

    private void ConfigureClient()
    {
        httpClient.BaseAddress ??= new Uri("https://api.github.com/");
        if (!httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hhnl-formicae", "0.1"));
        }

        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token) && httpClient.DefaultRequestHeaders.Authorization is null)
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private static WorkItem ToWorkItem(string issueUrl, GitHubIssueDto issue, IReadOnlyList<GitHubCommentDto> comments)
        => new(
            issueUrl,
            issue.Title,
            issue.Body ?? string.Empty,
            comments.Select(comment => comment.Body ?? string.Empty).ToArray(),
            (issue.Labels ?? []).Select(label => label.Name).ToArray());

    private sealed record GitHubIssueDto(
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("labels")] IReadOnlyList<GitHubLabelDto>? Labels,
        [property: JsonPropertyName("pull_request")] object? PullRequest);
    private sealed record GitHubLabelDto([property: JsonPropertyName("name")] string Name);
    private sealed record GitHubCommentDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("body")] string? Body);
    private sealed record UpsertIssueCommentRequest([property: JsonPropertyName("body")] string Body);
}

internal sealed record GitHubIssueReference(string Owner, string Repository, int Number)
{
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
