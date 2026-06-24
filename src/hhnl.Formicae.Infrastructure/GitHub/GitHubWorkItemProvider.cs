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

        return new WorkItem(issueUrl, issueResponse.Title, issueResponse.Body ?? string.Empty, comments.Select(comment => comment.Body ?? string.Empty).ToArray());
    }

    private void ConfigureClient()
    {
        httpClient.BaseAddress ??= new Uri("https://api.github.com/");
        if (!httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hhnl-formicae", "0.1"));
        }
    }

    private sealed record GitHubIssueDto([property: JsonPropertyName("title")] string Title, [property: JsonPropertyName("body")] string? Body);
    private sealed record GitHubCommentDto([property: JsonPropertyName("body")] string? Body);
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
