using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Tests.TestDoubles;

public sealed class MockDevOpsAdapter : IWorkItemProvider, ISourceControlProvider
{
    private readonly Dictionary<string, WorkItem> workItems = new(StringComparer.OrdinalIgnoreCase);

    public List<GetIssueCall> GetIssueCalls { get; } = [];
    public List<CreateBranchCall> CreateBranchCalls { get; } = [];
    public List<CreateDraftPullRequestCall> CreateDraftPullRequestCalls { get; } = [];

    public string DefaultBranchName { get; set; } = "formicae/mock-branch";
    public string DefaultPullRequestUrl { get; set; } = "https://devops.local/mock/pull-request";

    public MockDevOpsAdapter AddIssue(string issueUrl, string title, string body, params string[] comments)
        => AddIssueWithLabels(issueUrl, title, body, [WorkItemWorkflowLabels.ReadyToPlan, WorkItemWorkflowLabels.ReadyToImplement], comments);

    public MockDevOpsAdapter AddIssueWithLabels(string issueUrl, string title, string body, IReadOnlyList<string> labels, params string[] comments)
    {
        workItems[issueUrl] = new WorkItem(issueUrl, title, body, comments, labels);
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

    public Task<string> CreateBranchAsync(string repositoryUrl, string baseBranch, Guid workflowId, CancellationToken cancellationToken)
    {
        CreateBranchCalls.Add(new CreateBranchCall(repositoryUrl, baseBranch, workflowId));
        return Task.FromResult(DefaultBranchName);
    }

    public Task<PullRequestResult> CreateDraftPullRequestAsync(Workflow workflow, IReadOnlyList<TaskRun> taskRuns, CancellationToken cancellationToken)
    {
        CreateDraftPullRequestCalls.Add(new CreateDraftPullRequestCall(workflow.Id, workflow.RepositoryUrl, workflow.BranchName, taskRuns));
        return Task.FromResult(new PullRequestResult(DefaultPullRequestUrl));
    }
}

public sealed record GetIssueCall(string IssueUrl);

public sealed record CreateBranchCall(string RepositoryUrl, string BaseBranch, Guid WorkflowId);

public sealed record CreateDraftPullRequestCall(
    Guid WorkflowId,
    string RepositoryUrl,
    string? BranchName,
    IReadOnlyList<TaskRun> TaskRuns);
