using hhnl.Formicae.Application.Images;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace hhnl.Formicae.Application.Workflows;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnvironmentRuntimeSettings([property: JsonNumberHandling(JsonNumberHandling.Strict)] int? TimeoutLimitSeconds = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnvironmentConfiguration
{
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int SchemaVersion { get; init; } = 1;
    public EnvironmentRuntimeSettings? Runtime { get; init; }
    public EnvironmentImageSettings? Image { get; init; }
    public ImageSelection? ImageSelection { get; init; }
    public PreparedImageSnapshot? ImageSnapshot { get; init; }
    public IReadOnlyList<EnvironmentToolInstall> Tools { get; init; } = [];
    public IReadOnlyList<EnvironmentMcpServer> McpServers { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnvironmentImageSettings(string Reference, string PullPolicy = "IfNotPresent", IReadOnlyList<string>? PullSecretNames = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnvironmentToolInstall(string Name, string Script, string Shell = "sh", [property: JsonNumberHandling(JsonNumberHandling.Strict)] int TimeoutSeconds = 300);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnvironmentMcpServer(string Name, string Transport = "stdio", string? Command = null,
    IReadOnlyList<string>? Arguments = null, string? Url = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null, string? BearerTokenEnvironmentVariable = null,
    IReadOnlyDictionary<string, string>? HeaderEnvironmentVariables = null);

public sealed record EnvironmentSnapshot(string Id, int Revision, string Name, string Description, EnvironmentConfiguration Configuration);
public sealed record EnvironmentResponse(string Id, int Revision, string Name, string Description, EnvironmentConfiguration Configuration,
    bool BuiltIn, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record CreateEnvironmentRequest(string Name, string? Description = null, EnvironmentConfiguration? Configuration = null);
public sealed record UpdateEnvironmentRequest(int ExpectedRevision, string Name, string? Description = null, EnvironmentConfiguration? Configuration = null);
public sealed record EnvironmentDefinitionResolution(WorkflowDefinitionDocument Document, WorkflowDefinitionValidationResult Validation);
public sealed class EnvironmentConflictException(string message) : Exception(message);

public sealed record ExecutionEnvironmentProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string ConfigurationJson { get; init; } = "{\"schemaVersion\":1,\"runtime\":null,\"image\":null,\"tools\":[],\"mcpServers\":[]}";
    public int Revision { get; init; } = 1;
    public bool IsDeleted { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public interface IEnvironmentStore
{
    Task<ExecutionEnvironmentProfile?> GetAsync(string id, CancellationToken token);
    Task<IReadOnlyList<ExecutionEnvironmentProfile>> ListAsync(CancellationToken token);
    Task<ExecutionEnvironmentProfile> CreateAsync(ExecutionEnvironmentProfile environment, CancellationToken token);
    Task<bool> TryUpdateAsync(ExecutionEnvironmentProfile replacement, int expectedRevision, CancellationToken token);
}
