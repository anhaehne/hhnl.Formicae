using System.Text.Json;

namespace hhnl.Formicae.Application.Workflows;

public sealed record CustomTaskInputDefinition(string Name, string ValueType, bool Required = false, JsonElement? DefaultValue = null);
public sealed record CustomTaskRunnerSettings(string Kind = "agent", int TimeoutSeconds = 1800);
public sealed record CustomTaskSnapshot(string Id, int Revision, string Name, string Description, string PromptTemplate,
    IReadOnlyList<CustomTaskInputDefinition> Inputs, CustomTaskRunnerSettings Runner);
public sealed record WorkflowCustomTaskSettings(string TaskId, IReadOnlyDictionary<string, JsonElement>? Inputs = null, CustomTaskSnapshot? Snapshot = null);
public sealed record CustomTaskResponse(string Id, int Revision, string Name, string Description, string PromptTemplate,
    IReadOnlyList<CustomTaskInputDefinition> Inputs, CustomTaskRunnerSettings Runner, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record CreateCustomTaskRequest(string Name, string PromptTemplate, string? Description = null,
    IReadOnlyList<CustomTaskInputDefinition>? Inputs = null, CustomTaskRunnerSettings? Runner = null);
public sealed record UpdateCustomTaskRequest(int ExpectedRevision, string Name, string PromptTemplate, string? Description = null,
    IReadOnlyList<CustomTaskInputDefinition>? Inputs = null, CustomTaskRunnerSettings? Runner = null);
public sealed record PreparedCustomTaskExecution(string TaskId, int Revision, string Name,
    IReadOnlyDictionary<string, JsonElement> Inputs, IReadOnlyDictionary<string, JsonElement> WorkflowFields,
    int TimeoutSeconds, string Prompt, int FormatVersion = 1);
public sealed record CustomTaskDefinitionResolution(WorkflowDefinitionDocument Document, WorkflowDefinitionValidationResult Validation);
public sealed class CustomTaskConflictException(string message) : Exception(message);

public sealed record CustomTaskDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string PromptTemplate { get; init; }
    public string Description { get; init; } = "";
    public string InputsJson { get; init; } = "[]";
    public string RunnerJson { get; init; } = "{\"kind\":\"agent\",\"timeoutSeconds\":1800}";
    public int Revision { get; init; } = 1;
    public bool IsDeleted { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public interface ICustomTaskStore
{
    Task<CustomTaskDefinition?> GetAsync(string id, CancellationToken token);
    Task<IReadOnlyList<CustomTaskDefinition>> ListAsync(CancellationToken token);
    Task<CustomTaskDefinition> CreateAsync(CustomTaskDefinition task, CancellationToken token);
    Task<bool> TryUpdateAsync(CustomTaskDefinition replacement, int expectedRevision, CancellationToken token);
}
