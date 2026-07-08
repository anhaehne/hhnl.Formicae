using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using Microsoft.Extensions.Logging;

namespace hhnl.Formicae.Infrastructure.Gitea;

public sealed class GiteaDevOpsPlatform : IDevOpsPlatform
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly DevOpsIntegration integration;
    private readonly ILogger<GiteaDevOpsPlatform> logger;

    public GiteaDevOpsPlatform(IHttpClientFactory httpClientFactory, DevOpsIntegration integration, ILogger<GiteaDevOpsPlatform> logger)
    {
        this.integration = integration;
        this.logger = logger;
        httpClient = httpClientFactory.CreateClient(nameof(GiteaDevOpsPlatform));
        httpClient.BaseAddress = DevOpsReferenceParser.NormalizeServerUrl(DevOpsProviderType.Gitea, integration.ServerUrl);
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", integration.AccessToken);
    }

    public DevOpsProviderType ProviderType => DevOpsProviderType.Gitea;

    public async Task<DevOpsIssue> GetIssueAsync(DevOpsIssueReference issue, CancellationToken cancellationToken)
        => ToIssue(await GetJsonAsync<GiteaIssue>($"api/v1/repos/{E(issue.Owner)}/{E(issue.Repository)}/issues/{issue.Number}", cancellationToken));

    public async Task<IReadOnlyList<DevOpsIssue>> ListIssuesWithLabelAsync(DevOpsRepositoryReference repository, string label, CancellationToken cancellationToken)
    {
        var issues = await GetJsonAsync<List<GiteaIssue>>(
            $"api/v1/repos/{E(repository.Owner)}/{E(repository.Name)}/issues?state=open&labels={Uri.EscapeDataString(label)}",
            cancellationToken);
        return issues.Select(ToIssue).ToArray();
    }

    public async Task<IReadOnlyList<DevOpsComment>> ListIssueCommentsAsync(DevOpsIssueReference issue, CancellationToken cancellationToken)
    {
        var comments = await GetJsonAsync<List<GiteaComment>>(
            $"api/v1/repos/{E(issue.Owner)}/{E(issue.Repository)}/issues/{issue.Number}/comments",
            cancellationToken);
        return comments.Select(ToComment).ToArray();
    }

    public async Task CreateIssueCommentAsync(DevOpsIssueReference issue, string body, CancellationToken cancellationToken)
        => await SendJsonAsync(
            HttpMethod.Post,
            $"api/v1/repos/{E(issue.Owner)}/{E(issue.Repository)}/issues/{issue.Number}/comments",
            new { body },
            cancellationToken);

    public async Task UpdateIssueCommentAsync(DevOpsRepositoryReference repository, string commentId, string body, CancellationToken cancellationToken)
        => await SendJsonAsync(
            HttpMethod.Patch,
            $"api/v1/repos/{E(repository.Owner)}/{E(repository.Name)}/issues/comments/{E(commentId)}",
            new { body },
            cancellationToken);

    public Task ReactToIssueAsync(DevOpsIssueReference issue, string reaction, CancellationToken cancellationToken)
    {
        logger.LogInformation("Skipping Gitea issue reaction '{Reaction}' for {IssueUrl}; reactions are unavailable.", reaction, issue.IssueUrl);
        return Task.CompletedTask;
    }

    public Task ReactToIssueCommentAsync(DevOpsRepositoryReference repository, string commentId, string reaction, CancellationToken cancellationToken)
    {
        logger.LogInformation("Skipping Gitea issue comment reaction '{Reaction}' for {RepositoryUrl} comment {CommentId}; reactions are unavailable.", reaction, repository.RepositoryUrl, commentId);
        return Task.CompletedTask;
    }

    public async Task<string> GetBranchHeadShaAsync(DevOpsRepositoryReference repository, string branchName, CancellationToken cancellationToken)
    {
        var branch = await GetJsonAsync<GiteaBranch>($"api/v1/repos/{E(repository.Owner)}/{E(repository.Name)}/branches/{E(branchName)}", cancellationToken);
        return branch.Commit?.Id ?? throw new InvalidOperationException($"Gitea branch '{branchName}' did not include a commit id.");
    }

    public async Task<string> CreateBranchAsync(DevOpsRepositoryReference repository, DevOpsIssueReference? linkedIssue, string baseSha, string branchName, CancellationToken cancellationToken)
    {
        await SendJsonAsync(
            HttpMethod.Post,
            $"api/v1/repos/{E(repository.Owner)}/{E(repository.Name)}/branches",
            new { new_branch_name = branchName, old_ref_name = baseSha },
            cancellationToken);
        return branchName;
    }

    public async Task<IReadOnlyList<DevOpsPullRequest>> ListPullRequestsAsync(DevOpsRepositoryReference repository, string headOwner, string headBranch, CancellationToken cancellationToken)
    {
        var pulls = await GetJsonAsync<List<GiteaPullRequest>>(
            $"api/v1/repos/{E(repository.Owner)}/{E(repository.Name)}/pulls?state=open",
            cancellationToken);
        return pulls
            .Where(pull => string.Equals(pull.Head?.Ref, headBranch, StringComparison.OrdinalIgnoreCase))
            .Select(ToPullRequest)
            .ToArray();
    }

    public async Task<DevOpsPullRequest> GetPullRequestAsync(DevOpsPullRequestReference pullRequest, CancellationToken cancellationToken)
        => ToPullRequest(await GetJsonAsync<GiteaPullRequest>($"api/v1/repos/{E(pullRequest.Owner)}/{E(pullRequest.Repository)}/pulls/{pullRequest.Number}", cancellationToken));

    public async Task<DevOpsPullRequest> CreatePullRequestAsync(DevOpsRepositoryReference repository, string title, string head, string baseBranch, string body, CancellationToken cancellationToken)
        => ToPullRequest(await SendJsonAsync<GiteaPullRequest>(
            HttpMethod.Post,
            $"api/v1/repos/{E(repository.Owner)}/{E(repository.Name)}/pulls",
            new { title, head, @base = baseBranch, body },
            cancellationToken));

    public async Task<IReadOnlyList<DevOpsPullRequestComment>> ListPullRequestCommentsAsync(DevOpsPullRequestReference pullRequest, CancellationToken cancellationToken)
    {
        var issueComments = await GetJsonAsync<List<GiteaComment>>(
            $"api/v1/repos/{E(pullRequest.Owner)}/{E(pullRequest.Repository)}/issues/{pullRequest.Number}/comments",
            cancellationToken);
        return issueComments
            .Select(comment => new DevOpsPullRequestComment(
                $"issue:{comment.Id}",
                comment.User?.Login ?? "unknown",
                comment.Body ?? string.Empty,
                comment.HtmlUrl ?? pullRequest.PullRequestUrl,
                comment.UpdatedAt ?? comment.CreatedAt ?? DateTimeOffset.MinValue,
                PullRequestCommentKind.IssueComment))
            .ToArray();
    }

    public async Task CreatePullRequestCommentAsync(DevOpsPullRequestReference pullRequest, string body, CancellationToken cancellationToken)
        => await SendJsonAsync(
            HttpMethod.Post,
            $"api/v1/repos/{E(pullRequest.Owner)}/{E(pullRequest.Repository)}/issues/{pullRequest.Number}/comments",
            new { body },
            cancellationToken);

    public Task ReactToPullRequestCommentAsync(DevOpsPullRequestReference pullRequest, PullRequestComment comment, string reaction, CancellationToken cancellationToken)
    {
        logger.LogInformation("Skipping Gitea pull request comment reaction '{Reaction}' for {PullRequestUrl} comment {CommentId}; reactions are unavailable.", reaction, pullRequest.PullRequestUrl, comment.Id);
        return Task.CompletedTask;
    }

    public async Task<DevOpsRepositoryFile?> GetFileAsync(DevOpsRepositoryReference repository, string path, string reference, CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync($"api/v1/repos/{E(repository.Owner)}/{E(repository.Name)}/contents/{Path(path)}?ref={Uri.EscapeDataString(reference)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response);
        var file = await response.Content.ReadFromJsonAsync<GiteaContent>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Gitea content response was empty.");
        return new DevOpsRepositoryFile(path, file.Sha ?? string.Empty);
    }

    public async Task CreateFileAsync(DevOpsRepositoryReference repository, string path, string message, string content, string branch, CancellationToken cancellationToken)
        => await SendJsonAsync(
            HttpMethod.Post,
            $"api/v1/repos/{E(repository.Owner)}/{E(repository.Name)}/contents/{Path(path)}",
            new { message, content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)), branch },
            cancellationToken);

    public async Task UpdateFileAsync(DevOpsRepositoryReference repository, string path, string message, string content, string sha, string branch, CancellationToken cancellationToken)
        => await SendJsonAsync(
            HttpMethod.Put,
            $"api/v1/repos/{E(repository.Owner)}/{E(repository.Name)}/contents/{Path(path)}",
            new { message, content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)), sha, branch },
            cancellationToken);

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync(path, cancellationToken);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Gitea response was empty.");
    }

    private async Task SendJsonAsync(HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: JsonOptions) };
        var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response);
    }

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: JsonOptions) };
        var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Gitea response was empty.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.Conflict || response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            throw new InvalidOperationException($"Gitea request failed because the resource already exists: {body}");
        }

        throw new InvalidOperationException($"Gitea request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }

    private static DevOpsIssue ToIssue(GiteaIssue issue)
        => new(
            issue.HtmlUrl ?? string.Empty,
            issue.Title ?? string.Empty,
            issue.Body ?? string.Empty,
            issue.PullRequest is not null,
            (issue.Labels ?? []).Select(label => label.Name ?? string.Empty).Where(label => !string.IsNullOrWhiteSpace(label)).ToArray());

    private static DevOpsComment ToComment(GiteaComment comment)
        => new(
            comment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            comment.User?.Login ?? "unknown",
            comment.Body ?? string.Empty,
            comment.HtmlUrl ?? string.Empty,
            comment.UpdatedAt ?? comment.CreatedAt ?? DateTimeOffset.MinValue);

    private static DevOpsPullRequest ToPullRequest(GiteaPullRequest pullRequest)
        => new(
            pullRequest.HtmlUrl ?? string.Empty,
            pullRequest.Title ?? string.Empty,
            string.Equals(pullRequest.State, "open", StringComparison.OrdinalIgnoreCase),
            pullRequest.Merged == true);

    private static string E(string value) => Uri.EscapeDataString(value);

    private static string Path(string path)
        => string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private sealed record GiteaIssue(
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        string? Title,
        string? Body,
        IReadOnlyList<GiteaLabel>? Labels,
        [property: JsonPropertyName("pull_request")] JsonElement? PullRequest);

    private sealed record GiteaLabel(string? Name);

    private sealed record GiteaComment(
        long Id,
        GiteaUser? User,
        string? Body,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
        [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt);

    private sealed record GiteaUser(string? Login);

    private sealed record GiteaBranch(GiteaCommit? Commit);

    private sealed record GiteaCommit(string? Id);

    private sealed record GiteaPullRequest(
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        string? Title,
        string? State,
        bool? Merged,
        GiteaHead? Head);

    private sealed record GiteaHead(string? Ref);

    private sealed record GiteaContent(string? Sha);
}
