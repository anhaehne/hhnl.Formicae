using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Infrastructure.GitHub;

public sealed class GitHubSourceControlProvider : ISourceControlProvider
{
    public Task<string> CreateBranchAsync(string repositoryUrl, string baseBranch, Guid workflowId, CancellationToken cancellationToken)
        => Task.FromResult($"formicae/{workflowId:N}");

    public Task<PullRequestResult> CreateDraftPullRequestAsync(Workflow workflow, IReadOnlyList<TaskRun> taskRuns, CancellationToken cancellationToken)
    {
        var url = $"{workflow.RepositoryUrl.TrimEnd('/')}/pull/formicae-{workflow.Id:N}";
        return Task.FromResult(new PullRequestResult(url));
    }
}
