using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Tests.TestDoubles;

public sealed class MockDevOpsAdapter : IWorkItemProvider, ISourceControlProvider
{
    private readonly Dictionary<string, WorkItem> workItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PullRequestComment> pullRequestComments = [];
    private readonly List<string> issueComments = [];

    public List<GetIssueCall> GetIssueCalls { get; } = [];
    public List<ListIssuesWithLabelCall> ListIssuesWithLabelCalls { get; } = [];
    public List<UpsertIssueCommentCall> UpsertIssueCommentCalls { get; } = [];
    public List<ReactToIssueCall> ReactToIssueCalls { get; } = [];
    public List<CreateBranchCall> CreateBranchCalls { get; } = [];
    public List<CreatePullRequestCall> CreatePullRequestCalls { get; } = [];
    public List<ListPullRequestCommentsCall> ListPullRequestCommentsCalls { get; } = [];
    public List<UpsertPullRequestCommentCall> UpsertPullRequestCommentCalls { get; } = [];
    public List<ReactToPullRequestCommentCall> ReactToPullRequestCommentCalls { get; } = [];

    public string DefaultBranchName { get; set; } = "formicae/mock-branch";
    public string DefaultPullRequestUrl { get; set; } = "https://devops.local/mock/pull-request";

    public MockDevOpsAdapter AddIssue(string issueUrl, string title, string body, params string[] comments)
        => AddIssueWithLabels(issueUrl, title, body, [WorkItemWorkflowLabels.ReadyToPlan, WorkItemWorkflowLabels.ReadyToImplement], comments);

    public MockDevOpsAdapter AddIssueWithLabels(string issueUrl, string title, string body, IReadOnlyList<string> labels, params string[] comments)
    {
        workItems[issueUrl] = new WorkItem(issueUrl, title, body, comments, labels);
        return this;
    }

    public MockDevOpsAdapter AddPullRequestComment(
        string id,
        string author,
        string body,
        PullRequestCommentKind kind = PullRequestCommentKind.IssueComment,
        DateTimeOffset? updatedAt = null)
    {
        pullRequestComments.Add(new PullRequestComment(id, author, body, $"{DefaultPullRequestUrl}#comment-{id}", updatedAt ?? DateTimeOffset.UtcNow, kind));
        return this;
    }

    public Task<WorkItem> GetIssueAsync(string issueUrl, CancellationToken cancellationToken)
    {
        GetIssueCalls.Add(new GetIssueCall(issueUrl));

        if (workItems.TryGetValue(issueUrl, out var workItem))
        {
            return Task.FromResult(workItem);
        }

        return Task.FromResult(new WorkItem(
            issueUrl,
            "Mock DevOps work item",
            "Default mock work item body.",
            [],
            [WorkItemWorkflowLabels.ReadyToPlan, WorkItemWorkflowLabels.ReadyToImplement]));
    }

    public Task<IReadOnlyList<WorkItem>> ListIssuesWithLabelAsync(string repositoryUrl, string label, CancellationToken cancellationToken)
    {
        ListIssuesWithLabelCalls.Add(new ListIssuesWithLabelCall(repositoryUrl, label));
        return Task.FromResult<IReadOnlyList<WorkItem>>(workItems.Values
            .Where(workItem => workItem.Url.StartsWith(repositoryUrl.TrimEnd('/') + "/issues/", StringComparison.OrdinalIgnoreCase)
                && workItem.HasLabel(label))
            .OrderBy(workItem => workItem.Url, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    public Task UpsertIssueCommentAsync(string issueUrl, string marker, string body, CancellationToken cancellationToken)
    {
        UpsertIssueCommentCalls.Add(new UpsertIssueCommentCall(issueUrl, marker, body));
        issueComments.RemoveAll(comment => comment.Contains(marker, StringComparison.OrdinalIgnoreCase));
        issueComments.Add(body);
        return Task.CompletedTask;
    }

    public Task ReactToIssueAsync(string issueUrl, string reaction, CancellationToken cancellationToken)
    {
        ReactToIssueCalls.Add(new ReactToIssueCall(issueUrl, reaction));
        return Task.CompletedTask;
    }

    public Task<string> CreateBranchAsync(string repositoryUrl, string baseBranch, Guid workflowId, CancellationToken cancellationToken)
    {
        CreateBranchCalls.Add(new CreateBranchCall(repositoryUrl, baseBranch, workflowId));
        return Task.FromResult(DefaultBranchName);
    }

    public Task<PullRequestResult> CreatePullRequestAsync(Workflow workflow, IReadOnlyList<TaskRun> taskRuns, CancellationToken cancellationToken)
    {
        CreatePullRequestCalls.Add(new CreatePullRequestCall(workflow.Id, workflow.RepositoryUrl, workflow.BranchName, taskRuns));
        return Task.FromResult(new PullRequestResult(DefaultPullRequestUrl));
    }

    public Task<IReadOnlyList<PullRequestComment>> ListPullRequestCommentsAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        ListPullRequestCommentsCalls.Add(new ListPullRequestCommentsCall(workflow.Id, workflow.PullRequestUrl));
        return Task.FromResult<IReadOnlyList<PullRequestComment>>(pullRequestComments.Where(comment => !PullRequestCommentMarkers.IsAutomationComment(comment.Body)).ToArray());
    }

    public Task UpsertPullRequestCommentAsync(Workflow workflow, string body, CancellationToken cancellationToken)
    {
        UpsertPullRequestCommentCalls.Add(new UpsertPullRequestCommentCall(workflow.Id, workflow.PullRequestUrl, body));
        pullRequestComments.Add(new PullRequestComment(
            $"formicae:{workflow.Id:N}:address-comments",
            "formicae",
            body,
            $"{workflow.PullRequestUrl ?? DefaultPullRequestUrl}#formicae-address-comments",
            DateTimeOffset.UtcNow,
            PullRequestCommentKind.IssueComment));
        return Task.CompletedTask;
    }

    public Task ReactToPullRequestCommentAsync(Workflow workflow, PullRequestComment comment, string reaction, CancellationToken cancellationToken)
    {
        ReactToPullRequestCommentCalls.Add(new ReactToPullRequestCommentCall(workflow.Id, comment.Id, comment.Kind, reaction));
        return Task.CompletedTask;
    }
}

public sealed record GetIssueCall(string IssueUrl);

public sealed record ListIssuesWithLabelCall(string RepositoryUrl, string Label);

public sealed record UpsertIssueCommentCall(string IssueUrl, string Marker, string Body);

public sealed record ReactToIssueCall(string IssueUrl, string Reaction);

public sealed record CreateBranchCall(string RepositoryUrl, string BaseBranch, Guid WorkflowId);

public sealed record CreatePullRequestCall(
    Guid WorkflowId,
    string RepositoryUrl,
    string? BranchName,
    IReadOnlyList<TaskRun> TaskRuns);

public sealed record ListPullRequestCommentsCall(Guid WorkflowId, string? PullRequestUrl);

public sealed record UpsertPullRequestCommentCall(Guid WorkflowId, string? PullRequestUrl, string Body);

public sealed record ReactToPullRequestCommentCall(Guid WorkflowId, string CommentId, PullRequestCommentKind Kind, string Reaction);
