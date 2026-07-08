using System.Net;
using System.Text;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using Octokit;
using Workflow = hhnl.Formicae.Application.Workflows.Workflow;

namespace hhnl.Formicae.Infrastructure.DevOps;

public sealed class DevOpsSourceControlProvider(IDevOpsPlatformFactory platformFactory) : ISourceControlProvider
{
    public async Task<string> CreateBranchAsync(CreateBranchRequest request, CancellationToken cancellationToken)
    {
        var context = await platformFactory.CreateForRepositoryAsync(request.RepositoryUrl, cancellationToken);
        var issue = DevOpsReferenceParser.ParseIssueUrl(context.Integration.ProviderType, request.LinkedWorkItemUrl, context.Integration.ServerUrl);
        if (!string.Equals(context.Repository.Owner, issue.Owner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(context.Repository.Name, issue.Repository, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Linked work item URL must belong to the workflow repository.", nameof(request));
        }

        var baseSha = await context.Platform.GetBranchHeadShaAsync(context.Repository, request.BaseBranch, cancellationToken);
        try
        {
            return await context.Platform.CreateBranchAsync(context.Repository, issue, baseSha, request.BranchName, cancellationToken);
        }
        catch (ApiException exception) when (IsAlreadyExists(exception))
        {
            return request.BranchName;
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return request.BranchName;
        }
    }

    public async Task<PullRequestResult> CreatePullRequestAsync(Workflow workflow, IReadOnlyList<TaskRun> taskRuns, CancellationToken cancellationToken)
    {
        var context = await platformFactory.CreateForRepositoryAsync(workflow.RepositoryUrl, cancellationToken);
        var branchName = workflow.BranchName ?? throw new InvalidOperationException("Workflow branch is required before creating a pull request.");

        await UpsertWorkflowSummaryAsync(context, workflow, taskRuns, branchName, cancellationToken);

        var existing = await context.Platform.ListPullRequestsAsync(context.Repository, context.Repository.Owner, branchName, cancellationToken);
        var pullRequest = existing.FirstOrDefault();
        if (pullRequest is not null)
        {
            return new PullRequestResult(pullRequest.Url);
        }

        var title = await BuildPullRequestTitleAsync(context, workflow, cancellationToken);
        var body = BuildPullRequestBody(workflow, taskRuns);
        var created = await context.Platform.CreatePullRequestAsync(context.Repository, title, branchName, workflow.BaseBranch, body, cancellationToken);
        return new PullRequestResult(created.Url);
    }

    public async Task<IReadOnlyList<PullRequestComment>> ListPullRequestCommentsAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var context = await platformFactory.CreateForRepositoryAsync(workflow.RepositoryUrl, cancellationToken);
        var pullRequestUrl = workflow.PullRequestUrl ?? throw new InvalidOperationException("Workflow pull request URL is required before reading pull request comments.");
        var pullRequest = DevOpsReferenceParser.ParsePullRequestUrl(context.Integration.ProviderType, pullRequestUrl, context.Integration.ServerUrl);
        var comments = await context.Platform.ListPullRequestCommentsAsync(pullRequest, cancellationToken);
        return comments
            .Where(comment => !string.IsNullOrWhiteSpace(comment.Body))
            .Where(comment => !PullRequestCommentMarkers.IsAutomationComment(comment.Body))
            .OrderBy(comment => comment.UpdatedAt)
            .ThenBy(comment => comment.Id, StringComparer.Ordinal)
            .Select(comment => new PullRequestComment(comment.Id, comment.Author, comment.Body, comment.Url, comment.UpdatedAt, comment.Kind))
            .ToArray();
    }

    public async Task<PullRequestStatus> GetPullRequestStatusAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        var context = await platformFactory.CreateForRepositoryAsync(workflow.RepositoryUrl, cancellationToken);
        var pullRequestUrl = workflow.PullRequestUrl ?? throw new InvalidOperationException("Workflow pull request URL is required before reading pull request status.");
        var pullRequestReference = DevOpsReferenceParser.ParsePullRequestUrl(context.Integration.ProviderType, pullRequestUrl, context.Integration.ServerUrl);
        var pullRequest = await context.Platform.GetPullRequestAsync(pullRequestReference, cancellationToken);
        return new PullRequestStatus(pullRequest.IsOpen, pullRequest.IsMerged);
    }

    public async Task UpsertPullRequestCommentAsync(Workflow workflow, string body, CancellationToken cancellationToken)
    {
        var context = await platformFactory.CreateForRepositoryAsync(workflow.RepositoryUrl, cancellationToken);
        var pullRequestUrl = workflow.PullRequestUrl ?? throw new InvalidOperationException("Workflow pull request URL is required before writing pull request comments.");
        var pullRequest = DevOpsReferenceParser.ParsePullRequestUrl(context.Integration.ProviderType, pullRequestUrl, context.Integration.ServerUrl);
        await context.Platform.CreatePullRequestCommentAsync(pullRequest, body, cancellationToken);
    }

    public async Task ReactToPullRequestCommentAsync(Workflow workflow, PullRequestComment comment, string reaction, CancellationToken cancellationToken)
    {
        var context = await platformFactory.CreateForRepositoryAsync(workflow.RepositoryUrl, cancellationToken);
        var pullRequestUrl = workflow.PullRequestUrl ?? throw new InvalidOperationException("Workflow pull request URL is required before reacting to pull request comments.");
        var pullRequest = DevOpsReferenceParser.ParsePullRequestUrl(context.Integration.ProviderType, pullRequestUrl, context.Integration.ServerUrl);
        await context.Platform.ReactToPullRequestCommentAsync(pullRequest, comment, reaction, cancellationToken);
    }

    private static async Task UpsertWorkflowSummaryAsync(
        DevOpsPlatformContext context,
        Workflow workflow,
        IReadOnlyList<TaskRun> taskRuns,
        string branchName,
        CancellationToken cancellationToken)
    {
        var path = $".formicae/workflows/{workflow.Id:N}.md";
        var content = BuildWorkflowSummary(workflow, taskRuns);
        var message = $"Record Formicae workflow {workflow.Id:N}";
        var existing = await context.Platform.GetFileAsync(context.Repository, path, branchName, cancellationToken);
        if (existing is null)
        {
            await context.Platform.CreateFileAsync(context.Repository, path, message, content, branchName, cancellationToken);
            return;
        }

        await context.Platform.UpdateFileAsync(context.Repository, path, message, content, existing.Sha, branchName, cancellationToken);
    }

    private static async Task<string> BuildPullRequestTitleAsync(DevOpsPlatformContext context, Workflow workflow, CancellationToken cancellationToken)
    {
        var issue = DevOpsReferenceParser.ParseIssueUrl(context.Integration.ProviderType, workflow.IssueUrl, context.Integration.ServerUrl);
        if (!string.Equals(context.Repository.Owner, issue.Owner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(context.Repository.Name, issue.Repository, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Issue URL must belong to the workflow repository.", nameof(workflow));
        }

        var devOpsIssue = await context.Platform.GetIssueAsync(issue, cancellationToken);
        return string.IsNullOrWhiteSpace(devOpsIssue.Title) ? $"Issue #{issue.Number}" : devOpsIssue.Title.Trim();
    }

    private static string BuildPullRequestBody(Workflow workflow, IReadOnlyList<TaskRun> taskRuns)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Created by Formicae.");
        builder.AppendLine();
        builder.AppendLine($"Issue: {workflow.IssueUrl}");
        builder.AppendLine($"Workflow: `{workflow.Id:N}`");
        builder.AppendLine();
        builder.AppendLine("Task runs:");
        foreach (var run in taskRuns.OrderBy(run => run.CreatedAt))
        {
            builder.AppendLine($"- {run.Kind}: {run.Status}");
        }

        var implementation = taskRuns
            .OrderByDescending(run => run.UpdatedAt)
            .FirstOrDefault(run => run.Kind == TaskRunKind.Implement && !string.IsNullOrWhiteSpace(run.Output));
        if (implementation is not null)
        {
            builder.AppendLine();
            builder.AppendLine("## Implementation Summary");
            builder.AppendLine();
            builder.AppendLine(implementation.Output!.Trim());
        }

        return builder.ToString();
    }

    private static string BuildWorkflowSummary(Workflow workflow, IReadOnlyList<TaskRun> taskRuns)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Formicae Workflow");
        builder.AppendLine();
        builder.AppendLine($"- Workflow: `{workflow.Id:N}`");
        builder.AppendLine($"- Issue: {workflow.IssueUrl}");
        builder.AppendLine($"- Status: {workflow.Status}");
        builder.AppendLine();
        builder.AppendLine("## Task Runs");
        foreach (var run in taskRuns.OrderBy(run => run.CreatedAt))
        {
            builder.AppendLine();
            builder.AppendLine($"### {run.Kind}");
            builder.AppendLine();
            builder.AppendLine($"Status: {run.Status}");
            builder.AppendLine();
            builder.AppendLine("```text");
            builder.AppendLine(run.Output ?? string.Empty);
            builder.AppendLine("```");
        }

        return builder.ToString();
    }

    private static bool IsAlreadyExists(ApiException exception)
        => exception.StatusCode == HttpStatusCode.UnprocessableEntity
            || exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase);
}
