using System.Text.Json;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Application.Images;

public sealed record ImageSource(string Dockerfile = "", string? RepositoryUrl = null, string Ref = "main",
    string DockerfilePath = "Dockerfile", string ContextPath = ".", string? Target = null,
    IReadOnlyDictionary<string, string>? BuildArguments = null, string Platform = "linux/amd64");
public sealed record ImageRequest(string Name, string? Description, ImageSource Source, int? ExpectedRevision = null);
public sealed record ImageDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public int Revision { get; init; } = 1;
    public string SourceJson { get; init; } = "{}";
    public bool IsArchived { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
public sealed record ImageRevision
{
    public required string ImageId { get; init; }
    public int Revision { get; init; }
    public required string Name { get; init; }
    public required string SourceJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
public enum ImageBuildState { Queued, Building, Validating, Ready, Failed, Cancelled, TimedOut }
public sealed record ImageBuild
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string ImageId { get; init; }
    public required string ImageName { get; init; }
    public int SourceRevision { get; init; }
    public required string SourceJson { get; init; }
    public string? CommitSha { get; init; }
    public ImageBuildState State { get; init; }
    public int Revision { get; init; } = 1;
    public string? Reference { get; init; }
    public string? Failure { get; init; }
    public string Logs { get; init; } = "";
    public bool CleanupPending { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
}
public sealed record ImageResponse(string Id, string Name, string Description, int Revision, ImageSource Source,
    bool IsArchived, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record ImageBuildResponse(string Id, string ImageId, string ImageName, int SourceRevision, string State,
    string? CommitSha, string? Reference, string? Failure, string Logs, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record ImageSelection(string Mode = "inherit", string? ImageId = null, string? BuildId = null);
public sealed record PreparedImageSnapshot(string ImageId, string BuildId, int SourceRevision, string Name,
    string Reference, string PullPolicy = "IfNotPresent", IReadOnlyList<string>? PullSecretNames = null,
    int WorkerProtocolVersion = 1, string Platform = "linux/amd64");
public sealed class ImageConflictException(string message) : Exception(message);
public static class ImageJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static ImageSource Source(string json) => JsonSerializer.Deserialize<ImageSource>(json, Options)!;
    public static bool Terminal(ImageBuildState state) => state is ImageBuildState.Ready or ImageBuildState.Failed or ImageBuildState.Cancelled or ImageBuildState.TimedOut;
}
public interface IImageStore
{
    Task<ImageDefinition?> GetAsync(string id, CancellationToken token);
    Task<IReadOnlyList<ImageDefinition>> ListAsync(CancellationToken token);
    Task<IReadOnlyList<ImageRevision>> RevisionsAsync(string id, CancellationToken token);
    Task CreateAsync(ImageDefinition image, ImageRevision revision, CancellationToken token);
    Task<bool> UpdateAsync(ImageDefinition image, ImageRevision? revision, int expectedRevision, CancellationToken token);
    Task<ImageBuild?> BuildAsync(string id, CancellationToken token);
    Task<IReadOnlyList<ImageBuild>> BuildsAsync(string? imageId, CancellationToken token);
    Task AddBuildAsync(ImageBuild build, CancellationToken token);
    Task<bool> UpdateBuildAsync(ImageBuild build, int expectedRevision, CancellationToken token);
}
public interface IImageSourceResolver
{
    Task<string?> ResolveCommitAsync(ImageSource source, CancellationToken token);
}
public sealed record ImageBuildObservation(ImageBuildState State, string Logs = "", string? Reference = null, string? Failure = null);
public interface IImageBuildRuntime
{
    Task<ImageBuildObservation> ReconcileAsync(ImageBuild build, CancellationToken token);
    Task CleanupAsync(ImageBuild build, CancellationToken token);
}
