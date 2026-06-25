using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Infrastructure.Fakes;

public sealed class FakeWorkItemProvider : IWorkItemProvider
{
    public List<string> IssueComments { get; } = [];

    public Task<WorkItem> GetIssueAsync(string issueUrl, CancellationToken cancellationToken)
        => Task.FromResult(new WorkItem(issueUrl, "Fake GitHub issue", "This fake issue drives local MVP workflow tests.", ["Use fake adapters for fast iteration."], [WorkItemWorkflowLabels.ReadyToPlan, WorkItemWorkflowLabels.ReadyToImplement]));

    public Task<IReadOnlyList<WorkItem>> ListIssuesWithLabelAsync(string repositoryUrl, string label, CancellationToken cancellationToken)
    {
        if (!string.Equals(label, WorkItemWorkflowLabels.ReadyToPlan, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<IReadOnlyList<WorkItem>>([]);
        }

        return Task.FromResult<IReadOnlyList<WorkItem>>([
            new WorkItem(
                $"{repositoryUrl.TrimEnd('/')}/issues/1",
                "Fake discovered issue",
                "This fake issue was discovered from the ready-to-plan label.",
                [],
                [WorkItemWorkflowLabels.ReadyToPlan, WorkItemWorkflowLabels.ReadyToImplement])
        ]);
    }

    public Task UpsertIssueCommentAsync(string issueUrl, string marker, string body, CancellationToken cancellationToken)
    {
        IssueComments.RemoveAll(comment => comment.Contains(marker, StringComparison.OrdinalIgnoreCase));
        IssueComments.Add(body);
        return Task.CompletedTask;
    }

    public Task ReactToIssueAsync(string issueUrl, string reaction, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class FakeSourceControlProvider : ISourceControlProvider
{
    public Task<string> CreateBranchAsync(string repositoryUrl, string baseBranch, Guid workflowId, CancellationToken cancellationToken)
        => Task.FromResult($"formicae/{workflowId:N}");

    public Task<PullRequestResult> CreatePullRequestAsync(Workflow workflow, IReadOnlyList<TaskRun> taskRuns, CancellationToken cancellationToken)
        => Task.FromResult(new PullRequestResult($"{workflow.RepositoryUrl.TrimEnd('/')}/pull/formicae-{workflow.Id:N}"));

    public Task<IReadOnlyList<PullRequestComment>> ListPullRequestCommentsAsync(Workflow workflow, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<PullRequestComment>>([
            new PullRequestComment(
                $"fake-review-{workflow.Id:N}",
                "fake-reviewer",
                "Please address this fake review comment.",
                $"{workflow.PullRequestUrl ?? workflow.RepositoryUrl}#discussion_r1",
                DateTimeOffset.UtcNow,
                PullRequestCommentKind.ReviewComment)
        ]);

    public Task UpsertPullRequestCommentAsync(Workflow workflow, string body, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task ReactToPullRequestCommentAsync(Workflow workflow, PullRequestComment comment, string reaction, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class FakeAgentRunner : IAgentRunner
{
    public Task<AgentRunResult> RunAsync(AgentTask task, CancellationToken cancellationToken)
        => Task.FromResult(new AgentRunResult(true, $"fake-{task.Kind.ToString().ToLowerInvariant()}-{task.WorkflowId:N}", $"Fake {task.Kind} output for {task.RepositoryUrl} on {task.BranchName}.", null));
}
