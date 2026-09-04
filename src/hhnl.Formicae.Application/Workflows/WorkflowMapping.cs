namespace hhnl.Formicae.Application.Workflows;

public static class WorkflowMapping
{
    public static WorkflowSummaryResponse ToSummary(this Workflow workflow)
        => new(
            workflow.Id,
            workflow.IssueUrl,
            workflow.RepositoryUrl,
            workflow.Status,
            workflow.CurrentStep,
            workflow.CreatedAt,
            workflow.UpdatedAt,
            workflow.PullRequestUrl,
            workflow.FailureReason);

    public static TaskRunResponse ToResponse(this TaskRun run)
        => new(
            run.Id,
            run.WorkflowId,
            run.Kind,
            run.Status,
            run.ExternalId,
            run.Output,
            run.FailureReason,
            run.StartedAt,
            run.CompletedAt,
            run.CreatedAt,
            run.UpdatedAt,
            AgentMessageParser.Parse(run.Output));

    public static WorkflowEventResponse ToResponse(this WorkflowEvent evt)
        => new(
            evt.Id,
            evt.WorkflowId,
            evt.TaskRunId,
            evt.Type,
            evt.Level,
            evt.Message,
            evt.DetailsJson,
            evt.CreatedAt);

    public static WorkflowDefinitionResponse ToResponse(
        this WorkflowDefinition definition,
        IReadOnlyList<WorkflowDefinitionVersion> versions)
        => new(
            definition.Id,
            definition.Name,
            definition.CreatedAt,
            definition.UpdatedAt,
            versions.OrderByDescending(version => version.Version).Select(version => version.ToResponse()).ToArray());

    public static WorkflowDefinitionVersionResponse ToResponse(this WorkflowDefinitionVersion version)
        => new(
            version.Id,
            version.WorkflowDefinitionId,
            version.Version,
            version.DslSchemaVersion,
            version.IsEnabled,
            version.IsDefault,
            WorkflowDefinitionJson.Deserialize(version.DefinitionJson) ?? new WorkflowDefinitionDocument(version.DslSchemaVersion, string.Empty, []),
            version.CreatedAt);
}
