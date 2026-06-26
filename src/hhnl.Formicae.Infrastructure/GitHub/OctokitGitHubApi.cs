using System.Text.Json;
using System.Text.Json.Serialization;
using Octokit;

namespace hhnl.Formicae.Infrastructure.GitHub;

internal interface IGitHubApi
{
    Task<Issue> GetIssueAsync(string owner, string repository, int number);
    Task<IReadOnlyList<Issue>> ListIssuesWithLabelAsync(string owner, string repository, string label);
    Task<IReadOnlyList<IssueComment>> GetIssueCommentsAsync(string owner, string repository, int number);
    Task CreateIssueCommentAsync(string owner, string repository, int number, string body);
    Task UpdateIssueCommentAsync(string owner, string repository, long commentId, string body);
    Task ReactToIssueAsync(string owner, string repository, int number, string reaction);
    Task<Reference> GetReferenceAsync(string owner, string repository, string reference);
    Task<string> CreateLinkedBranchAsync(string owner, string repository, int issueNumber, string baseOid, string branchName, CancellationToken cancellationToken);
    Task CreateReferenceAsync(string owner, string repository, string branchName, string sha);
    Task<IReadOnlyList<PullRequest>> ListPullRequestsAsync(string owner, string repository, string headOwner, string headBranch);
    Task<PullRequest> CreatePullRequestAsync(string owner, string repository, string title, string head, string baseBranch, string body);
    Task<IReadOnlyList<PullRequestReviewComment>> GetPullRequestReviewCommentsAsync(string owner, string repository, int number);
    Task ReactToIssueCommentAsync(string owner, string repository, long commentId, string reaction);
    Task ReactToPullRequestReviewCommentAsync(string owner, string repository, long commentId, string reaction);
    Task<IReadOnlyList<RepositoryContent>> GetContentsByRefAsync(string owner, string repository, string path, string reference);
    Task CreateFileAsync(string owner, string repository, string path, string message, string content, string branch);
    Task UpdateFileAsync(string owner, string repository, string path, string message, string content, string sha, string branch);
}

internal sealed class OctokitGitHubApi(GitHubClient client) : IGitHubApi
{
    public Task<Issue> GetIssueAsync(string owner, string repository, int number)
        => client.Issue.Get(owner, repository, number);

    public Task<IReadOnlyList<Issue>> ListIssuesWithLabelAsync(string owner, string repository, string label)
    {
        var request = new RepositoryIssueRequest { State = ItemStateFilter.Open };
        request.Labels.Add(label);
        return client.Issue.GetAllForRepository(owner, repository, request);
    }

    public Task<IReadOnlyList<IssueComment>> GetIssueCommentsAsync(string owner, string repository, int number)
        => client.Issue.Comment.GetAllForIssue(owner, repository, number);

    public async Task CreateIssueCommentAsync(string owner, string repository, int number, string body)
        => await client.Issue.Comment.Create(owner, repository, number, body);

    public async Task UpdateIssueCommentAsync(string owner, string repository, long commentId, string body)
        => await client.Issue.Comment.Update(owner, repository, commentId, body);

    public async Task ReactToIssueAsync(string owner, string repository, int number, string reaction)
        => await client.Reaction.Issue.Create(owner, repository, number, new NewReaction(ToReactionType(reaction)));

    public Task<Reference> GetReferenceAsync(string owner, string repository, string reference)
        => client.Git.Reference.Get(owner, repository, reference);

    public async Task<string> CreateLinkedBranchAsync(string owner, string repository, int issueNumber, string baseOid, string branchName, CancellationToken cancellationToken)
    {
        var issueId = await GetIssueNodeIdAsync(owner, repository, issueNumber, cancellationToken);
        const string mutation = """
            mutation($issueId: ID!, $oid: GitObjectID!, $name: String!) {
              createLinkedBranch(input: { issueId: $issueId, oid: $oid, name: $name }) {
                linkedBranch {
                  ref {
                    name
                  }
                }
              }
            }
            """;
        var response = await PostGraphQlAsync(mutation, new { issueId, oid = baseOid, name = branchName }, cancellationToken);
        if (response.Data.TryGetProperty("createLinkedBranch", out var createLinkedBranch)
            && createLinkedBranch.TryGetProperty("linkedBranch", out var linkedBranch)
            && linkedBranch.TryGetProperty("ref", out var branchRef)
            && branchRef.TryGetProperty("name", out var name))
        {
            return name.GetString() ?? branchName;
        }

        return branchName;
    }

    public async Task CreateReferenceAsync(string owner, string repository, string branchName, string sha)
        => await client.Git.Reference.Create(owner, repository, new NewReference($"refs/heads/{branchName}", sha));

    public Task<IReadOnlyList<PullRequest>> ListPullRequestsAsync(string owner, string repository, string headOwner, string headBranch)
    {
        var request = new PullRequestRequest
        {
            State = ItemStateFilter.Open,
            Head = $"{headOwner}:{headBranch}"
        };
        return client.PullRequest.GetAllForRepository(owner, repository, request);
    }

    public Task<PullRequest> CreatePullRequestAsync(string owner, string repository, string title, string head, string baseBranch, string body)
    {
        var request = new NewPullRequest(title, head, baseBranch)
        {
            Body = body,
            Draft = false
        };
        return client.PullRequest.Create(owner, repository, request);
    }

    public Task<IReadOnlyList<PullRequestReviewComment>> GetPullRequestReviewCommentsAsync(string owner, string repository, int number)
        => client.PullRequest.ReviewComment.GetAll(owner, repository, number);

    public async Task ReactToIssueCommentAsync(string owner, string repository, long commentId, string reaction)
        => await client.Reaction.IssueComment.Create(owner, repository, commentId, new NewReaction(ToReactionType(reaction)));

    public async Task ReactToPullRequestReviewCommentAsync(string owner, string repository, long commentId, string reaction)
        => await client.Reaction.PullRequestReviewComment.Create(owner, repository, commentId, new NewReaction(ToReactionType(reaction)));

    public Task<IReadOnlyList<RepositoryContent>> GetContentsByRefAsync(string owner, string repository, string path, string reference)
        => client.Repository.Content.GetAllContentsByRef(owner, repository, path, reference);

    public async Task CreateFileAsync(string owner, string repository, string path, string message, string content, string branch)
        => await client.Repository.Content.CreateFile(owner, repository, path, new CreateFileRequest(message, content, branch));

    public async Task UpdateFileAsync(string owner, string repository, string path, string message, string content, string sha, string branch)
        => await client.Repository.Content.UpdateFile(owner, repository, path, new UpdateFileRequest(message, content, sha, branch));

    private async Task<string> GetIssueNodeIdAsync(string owner, string repository, int issueNumber, CancellationToken cancellationToken)
    {
        const string query = """
            query($owner: String!, $name: String!, $number: Int!) {
              repository(owner: $owner, name: $name) {
                issue(number: $number) {
                  id
                }
              }
            }
            """;
        var response = await PostGraphQlAsync(query, new { owner, name = repository, number = issueNumber }, cancellationToken);
        if (response.Data.TryGetProperty("repository", out var repositoryElement)
            && repositoryElement.TryGetProperty("issue", out var issueElement)
            && issueElement.TryGetProperty("id", out var idElement)
            && !string.IsNullOrWhiteSpace(idElement.GetString()))
        {
            return idElement.GetString()!;
        }

        throw new InvalidOperationException("GitHub GraphQL issue response did not include an issue id.");
    }

    private async Task<GraphQlResponse> PostGraphQlAsync(string query, object variables, CancellationToken cancellationToken)
    {
        var response = await client.Connection.Post<GraphQlResponse>(
            new Uri("graphql", UriKind.Relative),
            new { query, variables },
            "application/json",
            "application/json",
            new Dictionary<string, string>(),
            cancellationToken);

        if (response.Body.Errors is { Count: > 0 })
        {
            throw new InvalidOperationException($"GitHub GraphQL call failed: {string.Join("; ", response.Body.Errors.Select(error => error.Message))}");
        }

        return response.Body;
    }

    private static ReactionType ToReactionType(string reaction)
        => reaction switch
        {
            "+1" => ReactionType.Plus1,
            "-1" => ReactionType.Minus1,
            "laugh" => ReactionType.Laugh,
            "confused" => ReactionType.Confused,
            "heart" => ReactionType.Heart,
            "hooray" => ReactionType.Hooray,
            "rocket" => ReactionType.Rocket,
            "eyes" => ReactionType.Eyes,
            _ => throw new ArgumentException($"Unsupported GitHub reaction '{reaction}'.", nameof(reaction))
        };

    private sealed record GraphQlResponse(
        [property: JsonPropertyName("data")] JsonElement Data,
        [property: JsonPropertyName("errors")] IReadOnlyList<GraphQlError>? Errors);

    private sealed record GraphQlError([property: JsonPropertyName("message")] string Message);
}
