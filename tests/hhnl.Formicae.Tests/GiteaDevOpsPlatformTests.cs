using System.Net;
using System.Text;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Gitea;
using Microsoft.Extensions.Logging.Abstractions;

namespace hhnl.Formicae.Tests;

public sealed class GiteaDevOpsPlatformTests
{
    [Fact]
    public async Task GetIssueAsync_applies_auth_header_and_uses_custom_server_url()
    {
        var handler = new RecordingHandler("""{"html_url":"https://gitea.example/acme/widgets/issues/7","title":"Fix bug","body":"Body","labels":[{"name":"ready-to-plan"}]}""");
        var platform = CreatePlatform(handler);

        var issue = await platform.GetIssueAsync(
            DevOpsReferenceParser.ParseIssueUrl(DevOpsProviderType.Gitea, "https://gitea.example/acme/widgets/issues/7", "https://gitea.example"),
            CancellationToken.None);

        Assert.Equal("Fix bug", issue.Title);
        Assert.Equal("https://gitea.example/api/v1/repos/acme/widgets/issues/7", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("token", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal("access-token", handler.LastRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task Upsert_comment_flow_lists_then_updates_existing_comment()
    {
        var handler = new RecordingHandler("""[{"id":42,"body":"<!-- marker --> old","html_url":"https://gitea.example/comment/42","user":{"login":"alice"},"updated_at":"2026-07-08T00:00:00Z"}]""", "{}");
        var platform = CreatePlatform(handler);
        var issue = DevOpsReferenceParser.ParseIssueUrl(DevOpsProviderType.Gitea, "https://gitea.example/acme/widgets/issues/7", "https://gitea.example");
        var repository = DevOpsReferenceParser.ParseRepositoryUrl(DevOpsProviderType.Gitea, "https://gitea.example/acme/widgets", "https://gitea.example");

        var comments = await platform.ListIssueCommentsAsync(issue, CancellationToken.None);
        await platform.UpdateIssueCommentAsync(repository, comments.Single().Id, "new body", CancellationToken.None);

        Assert.Equal(HttpMethod.Patch, handler.Requests.Last().Method);
        Assert.Equal("https://gitea.example/api/v1/repos/acme/widgets/issues/comments/42", handler.Requests.Last().RequestUri!.ToString());
    }

    [Fact]
    public async Task CreateBranchAsync_posts_branch_payload()
    {
        var handler = new RecordingHandler("{}");
        var platform = CreatePlatform(handler);
        var repository = DevOpsReferenceParser.ParseRepositoryUrl(DevOpsProviderType.Gitea, "https://gitea.example/acme/widgets", "https://gitea.example");

        var branch = await platform.CreateBranchAsync(repository, null, "base-sha", "formicae/test", CancellationToken.None);

        Assert.Equal("formicae/test", branch);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("\"new_branch_name\":\"formicae/test\"", handler.LastBody);
        Assert.Contains("\"old_ref_name\":\"base-sha\"", handler.LastBody);
    }

    [Fact]
    public async Task File_create_and_update_use_content_endpoints()
    {
        var handler = new RecordingHandler("{}", "{}");
        var platform = CreatePlatform(handler);
        var repository = DevOpsReferenceParser.ParseRepositoryUrl(DevOpsProviderType.Gitea, "https://gitea.example/acme/widgets", "https://gitea.example");

        await platform.CreateFileAsync(repository, ".formicae/workflows/1.md", "create", "hello", "main", CancellationToken.None);
        await platform.UpdateFileAsync(repository, ".formicae/workflows/1.md", "update", "hello again", "sha", "main", CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.All(handler.Requests, request => Assert.Contains("/contents/.formicae/workflows/1.md", request.RequestUri!.ToString()));
    }

    [Fact]
    public async Task Pull_request_create_and_reuse_paths_are_supported()
    {
        var handler = new RecordingHandler(
            """[{"html_url":"https://gitea.example/acme/widgets/pulls/5","title":"Existing","state":"open","merged":false,"head":{"ref":"formicae/test"}}]""",
            """{"html_url":"https://gitea.example/acme/widgets/pulls/6","title":"New","state":"open","merged":false}""");
        var platform = CreatePlatform(handler);
        var repository = DevOpsReferenceParser.ParseRepositoryUrl(DevOpsProviderType.Gitea, "https://gitea.example/acme/widgets", "https://gitea.example");

        var existing = await platform.ListPullRequestsAsync(repository, "acme", "formicae/test", CancellationToken.None);
        var created = await platform.CreatePullRequestAsync(repository, "New", "formicae/new", "main", "body", CancellationToken.None);

        Assert.Single(existing);
        Assert.Equal("https://gitea.example/acme/widgets/pulls/6", created.Url);
        Assert.Equal(HttpMethod.Post, handler.Requests.Last().Method);
    }

    [Fact]
    public async Task Reactions_are_noops()
    {
        var handler = new RecordingHandler();
        var platform = CreatePlatform(handler);
        var issue = DevOpsReferenceParser.ParseIssueUrl(DevOpsProviderType.Gitea, "https://gitea.example/acme/widgets/issues/7", "https://gitea.example");

        await platform.ReactToIssueAsync(issue, "+1", CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    private static GiteaDevOpsPlatform CreatePlatform(RecordingHandler handler)
        => new(
            new SingleClientFactory(new HttpClient(handler)),
            new DevOpsIntegration
            {
                ProviderType = DevOpsProviderType.Gitea,
                ServerUrl = "https://gitea.example",
                AccessToken = "access-token"
            },
            NullLogger<GiteaDevOpsPlatform>.Instance);

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> responses = new(responses);
        public List<HttpRequestMessage> Requests { get; } = [];
        public HttpRequestMessage? LastRequest => Requests.LastOrDefault();
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(CloneRequest(request));
            var content = this.responses.Count == 0 ? "{}" : this.responses.Dequeue();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }
    }
}
