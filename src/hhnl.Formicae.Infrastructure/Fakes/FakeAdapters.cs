using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Infrastructure.Fakes;

public sealed class FakeWorkItemProvider : IWorkItemProvider
{
    public Task<WorkItem> GetIssueAsync(string issueUrl, CancellationToken cancellationToken)
        => Task.FromResult(new WorkItem(issueUrl, "Fake GitHub issue", "This fake issue drives local MVP workflow tests.", ["Use fake adapters for fast iteration."], [WorkItemWorkflowLabels.ReadyToPlan, WorkItemWorkflowLabels.ReadyToImplement]));
}

public sealed class FakeSourceControlProvider : ISourceControlProvider
{
    public Task<string> CreateBranchAsync(string repositoryUrl, string baseBranch, Guid workflowId, CancellationToken cancellationToken)
        => Task.FromResult($"formicae/{workflowId:N}");

    public Task<PullRequestResult> CreateDraftPullRequestAsync(Workflow workflow, IReadOnlyList<TaskRun> taskRuns, CancellationToken cancellationToken)
        => Task.FromResult(new PullRequestResult($"{workflow.RepositoryUrl.TrimEnd('/')}/pull/formicae-{workflow.Id:N}"));
}

public sealed class FakeAgentRunner : IAgentRunner
{
    public Task<AgentRunResult> RunAsync(AgentTask task, CancellationToken cancellationToken)
        => Task.FromResult(new AgentRunResult(true, $"fake-{task.Kind.ToString().ToLowerInvariant()}-{task.WorkflowId:N}", $"Fake {task.Kind} output for {task.RepositoryUrl} on {task.BranchName}.", null));
}
