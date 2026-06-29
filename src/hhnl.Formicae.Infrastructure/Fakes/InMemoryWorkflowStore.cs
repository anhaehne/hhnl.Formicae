using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Infrastructure.Fakes;

public sealed class InMemoryWorkflowStore : IWorkflowStore
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Workflow> workflows = [];
    private readonly Dictionary<Guid, TaskRun> runs = [];
    private readonly List<WorkflowEvent> events = [];
    private readonly List<WorkflowLog> logs = [];
    private readonly Dictionary<Guid, WorkflowDefinition> definitions = [];
    private readonly Dictionary<Guid, WorkflowDefinitionVersion> definitionVersions = [];

    public Task<Workflow> CreateWorkflowAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            workflows.Add(workflow.Id, workflow);
        }

        return Task.FromResult(workflow);
    }

    public Task<Workflow?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(workflows.GetValueOrDefault(workflowId));
        }
    }

    public Task<Workflow?> GetWorkflowByIssueUrlAsync(string issueUrl, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(workflows.Values.SingleOrDefault(workflow => string.Equals(workflow.IssueUrl, issueUrl, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public Task<IReadOnlyList<Workflow>> ListRecentWorkflowsAsync(int limit, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<Workflow>>(workflows.Values
                .OrderByDescending(workflow => workflow.CreatedAt)
                .Take(limit)
                .ToArray());
        }
    }

    public Task<Workflow?> GetWorkflowByPullRequestUrlAsync(string pullRequestUrl, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(workflows.Values.SingleOrDefault(workflow => string.Equals(workflow.PullRequestUrl, pullRequestUrl, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public Task<IReadOnlyList<Workflow>> ListRunnableWorkflowsAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<Workflow>>(workflows.Values
                .Where(workflow => workflow.Status is WorkflowStatus.Queued or WorkflowStatus.Planning or WorkflowStatus.Implementing or WorkflowStatus.CreatingPullRequest or WorkflowStatus.Reviewing)
                .OrderBy(workflow => workflow.CreatedAt)
                .ToArray());
        }
    }

    public Task<IReadOnlyList<Workflow>> ListNonTerminalWorkflowsAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<Workflow>>(workflows.Values
                .Where(workflow => workflow.Status is not WorkflowStatus.Completed and not WorkflowStatus.Failed and not WorkflowStatus.Canceled)
                .OrderBy(workflow => workflow.CreatedAt)
                .ToArray());
        }
    }

    public Task UpdateWorkflowAsync(Workflow workflow, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            workflows[workflow.Id] = workflow;
        }

        return Task.CompletedTask;
    }

    public Task<TaskRun> UpsertTaskRunAsync(TaskRun taskRun, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            runs[taskRun.Id] = taskRun;
        }

        return Task.FromResult(taskRun);
    }

    public Task<TaskRun?> GetTaskRunAsync(Guid workflowId, TaskRunKind kind, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(runs.Values.SingleOrDefault(run => run.WorkflowId == workflowId && run.Kind == kind));
        }
    }

    public Task<IReadOnlyList<TaskRun>> ListTaskRunsAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<TaskRun>>(runs.Values
                .Where(run => run.WorkflowId == workflowId)
                .OrderBy(run => run.CreatedAt)
                .ToArray());
        }
    }

    public Task AddEventAsync(WorkflowEvent evt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            events.Add(evt);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkflowEvent>> ListEventsAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<WorkflowEvent>>(events
                .Where(evt => evt.WorkflowId == workflowId)
                .OrderByDescending(evt => evt.CreatedAt)
                .ThenByDescending(evt => evt.Id)
                .ToArray());
        }
    }

    public Task AddLogAsync(WorkflowLog log, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            logs.Add(log);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkflowLog>> ListLogsAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<WorkflowLog>>(logs
                .Where(log => log.WorkflowId == workflowId)
                .OrderBy(log => log.CreatedAt)
                .ToArray());
        }
    }

    public Task<IReadOnlyList<WorkflowDefinition>> ListWorkflowDefinitionsAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<WorkflowDefinition>>(definitions.Values
                .OrderBy(definition => definition.Name)
                .ToArray());
        }
    }

    public Task<WorkflowDefinition?> GetWorkflowDefinitionAsync(Guid definitionId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(definitions.GetValueOrDefault(definitionId));
        }
    }

    public Task<WorkflowDefinition> CreateWorkflowDefinitionAsync(WorkflowDefinition definition, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            definitions.Add(definition.Id, definition);
        }

        return Task.FromResult(definition);
    }

    public Task<IReadOnlyList<WorkflowDefinitionVersion>> ListWorkflowDefinitionVersionsAsync(Guid definitionId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<WorkflowDefinitionVersion>>(definitionVersions.Values
                .Where(version => version.WorkflowDefinitionId == definitionId)
                .OrderByDescending(version => version.Version)
                .ToArray());
        }
    }

    public Task<WorkflowDefinitionVersion?> GetWorkflowDefinitionVersionAsync(Guid versionId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(definitionVersions.GetValueOrDefault(versionId));
        }
    }

    public Task<WorkflowDefinitionVersion?> GetLatestWorkflowDefinitionVersionAsync(Guid definitionId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(definitionVersions.Values
                .Where(version => version.WorkflowDefinitionId == definitionId)
                .OrderByDescending(version => version.Version)
                .FirstOrDefault());
        }
    }

    public Task<WorkflowDefinitionVersion?> GetLatestEnabledWorkflowDefinitionVersionAsync(Guid definitionId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(definitionVersions.Values
                .Where(version => version.WorkflowDefinitionId == definitionId && version.IsEnabled)
                .OrderByDescending(version => version.Version)
                .FirstOrDefault());
        }
    }

    public Task<WorkflowDefinitionVersion?> GetDefaultEnabledWorkflowDefinitionVersionAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(definitionVersions.Values
                .Where(version => version is { IsDefault: true, IsEnabled: true })
                .OrderByDescending(version => version.CreatedAt)
                .FirstOrDefault());
        }
    }

    public Task<WorkflowDefinitionVersion> CreateWorkflowDefinitionVersionAsync(WorkflowDefinitionVersion version, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (version.IsDefault)
            {
                foreach (var current in definitionVersions.Values)
                {
                    current.IsDefault = false;
                }
            }

            definitionVersions.Add(version.Id, version);
        }

        return Task.FromResult(version);
    }

    public Task EnsureDefaultWorkflowDefinitionAsync(
        WorkflowDefinition definition,
        WorkflowDefinitionVersion version,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (definitionVersions.Values.Any(candidate => candidate is { IsDefault: true, IsEnabled: true }))
            {
                return Task.CompletedTask;
            }

            definitions.TryAdd(definition.Id, definition);
            definitionVersions.TryAdd(version.Id, version);
        }

        return Task.CompletedTask;
    }
}
