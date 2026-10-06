namespace hhnl.Formicae.Application.Workflows;

// Shared query semantics keep the local fake and the SQL store behavior identical.
public static class WorkflowExecutionQueries
{
    public static IQueryable<Workflow> Filter(IQueryable<Workflow> workflows, WorkflowSearchQuery query)
    {
        if (query.Status is { } status) workflows = workflows.Where(item => item.Status == status);
        if (query.Active) workflows = workflows.Where(item => item.Status != WorkflowStatus.Completed
            && item.Status != WorkflowStatus.Failed && item.Status != WorkflowStatus.Canceled);
        if (query.DefinitionId is { } definitionId) workflows = workflows.Where(item => item.WorkflowDefinitionId == definitionId);
        if (!string.IsNullOrWhiteSpace(query.RepositoryUrl)) workflows = workflows.Where(item => item.RepositoryUrl == query.RepositoryUrl);
        if (query.From is { } from) workflows = workflows.Where(item => item.CreatedAt >= from);
        if (query.To is { } to) workflows = workflows.Where(item => item.CreatedAt <= to);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.ToLowerInvariant();
            var hasId = Guid.TryParse(query.Search, out var workflowId);
            workflows = workflows.Where(item => (hasId && item.Id == workflowId) || item.IssueUrl.ToLower().Contains(term)
                || item.RepositoryUrl.ToLower().Contains(term)
                || (item.BranchName != null && item.BranchName.ToLower().Contains(term))
                || (item.FailureReason != null && item.FailureReason.ToLower().Contains(term))
                || (item.PullRequestUrl != null && item.PullRequestUrl.ToLower().Contains(term)));
        }
        return workflows;
    }
    public static IQueryable<WorkflowLog> Filter(IQueryable<WorkflowLog> logs, Guid workflowId, WorkflowLogQuery query)
    {
        logs = logs.Where(log => log.WorkflowId == workflowId);
        if (query.TaskRunId is { } taskId) logs = logs.Where(log => log.TaskRunId == taskId);
        if (query.ExecutionAttemptId is { } attemptId) logs = logs.Where(log => log.ExecutionAttemptId == attemptId);
        if (!string.IsNullOrWhiteSpace(query.Level)) logs = logs.Where(log => log.Level == query.Level);
        if (!string.IsNullOrWhiteSpace(query.Source)) logs = logs.Where(log => log.Source == query.Source);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.ToLowerInvariant();
            logs = logs.Where(log => log.Message.ToLower().Contains(term));
        }
        return logs;
    }
    public static WorkflowLogQuery Validate(WorkflowLogQuery query)
    {
        if (query.After is < 0 || query.Before is < 0 || (query.After is not null && query.Before is not null))
            throw new ArgumentException("Use one nonnegative before or after cursor.");
        if (query.Search?.Length > 256) throw new ArgumentException("Log search must be at most 256 characters.");
        return query with { Limit = Math.Clamp(query.Limit, 1, 1000) };
    }
    public static WorkflowSearchQuery Validate(WorkflowSearchQuery query)
    {
        if (query.Offset < 0 || query.Offset > 1000000) throw new ArgumentException("Offset must be between 0 and 1000000.");
        if (query.From > query.To) throw new ArgumentException("From must precede To.");
        if (query.Search?.Length > 256) throw new ArgumentException("Search must be at most 256 characters.");
        return query with { Limit = Math.Clamp(query.Limit, 1, 200) };
    }
}
