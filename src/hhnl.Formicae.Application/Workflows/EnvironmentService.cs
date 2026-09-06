using System.Text.Json;

namespace hhnl.Formicae.Application.Workflows;

public sealed class EnvironmentService(IEnvironmentStore store, IClock? clock = null)
{
    public const string DefaultEnvironmentId = "default";
    public static EnvironmentSnapshot DefaultSnapshot { get; } = new(DefaultEnvironmentId, 1, "Default environment", "", new());
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static EnvironmentResponse DefaultResponse => new(DefaultEnvironmentId, 1, DefaultSnapshot.Name, "", DefaultSnapshot.Configuration,
        true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    private readonly IClock clock = clock ?? new SystemClock();

    public async Task<EnvironmentResponse?> GetAsync(string id, CancellationToken token)
        => id == DefaultEnvironmentId ? DefaultResponse : (await store.GetAsync(id, token)) is { } environment ? Response(environment) : null;
    public async Task<IReadOnlyList<EnvironmentResponse>> ListAsync(CancellationToken token)
        => new[] { DefaultResponse }.Concat((await store.ListAsync(token)).Where(environment => environment.Id != DefaultEnvironmentId)
            .OrderBy(environment => environment.Name, StringComparer.OrdinalIgnoreCase).ThenBy(environment => environment.Id, StringComparer.Ordinal).Select(Response)).ToArray();
    public async Task<EnvironmentResponse> CreateAsync(CreateEnvironmentRequest request, CancellationToken token)
    {
        var now = clock.UtcNow;
        var environment = Normalize(new ExecutionEnvironmentProfile { Id = Guid.NewGuid().ToString("N"), Name = request.Name,
            CreatedAt = now, UpdatedAt = now }, request.Name, request.Description, request.Configuration);
        return Response(await store.CreateAsync(environment, token));
    }
    public async Task<EnvironmentResponse?> UpdateAsync(string id, UpdateEnvironmentRequest request, CancellationToken token)
    {
        EnsureMutable(id); EnsureRevision(request.ExpectedRevision);
        var existing = await store.GetAsync(id, token);
        if (existing is null) return null;
        if (existing.Revision != request.ExpectedRevision) throw Conflict();
        var replacement = Normalize(existing with { Revision = checked(existing.Revision + 1), UpdatedAt = clock.UtcNow },
            request.Name, request.Description, request.Configuration);
        if (!await store.TryUpdateAsync(replacement, request.ExpectedRevision, token)) throw Conflict();
        return Response(replacement);
    }
    public async Task<bool> DeleteAsync(string id, int expectedRevision, CancellationToken token)
    {
        EnsureMutable(id); EnsureRevision(expectedRevision);
        var existing = await store.GetAsync(id, token);
        if (existing is null) return false;
        if (existing.Revision != expectedRevision) throw Conflict();
        if (!await store.TryUpdateAsync(existing with { IsDeleted = true, Revision = checked(existing.Revision + 1), UpdatedAt = clock.UtcNow }, expectedRevision, token)) throw Conflict();
        return true;
    }
    private static ExecutionEnvironmentProfile Normalize(ExecutionEnvironmentProfile environment, string? name, string? description, EnvironmentConfiguration? configuration)
    {
        name = name?.Trim() ?? ""; description = description?.Trim() ?? "";
        if (name.Length is 0 or > 120) throw new ArgumentException("Environment name is required and must contain at most 120 characters.");
        if (description.Length > 2000) throw new ArgumentException("Description must contain at most 2000 characters.");
        configuration ??= new();
        var validation = EnvironmentDefinitions.ValidateConfiguration(configuration);
        if (!validation.IsValid) throw new ArgumentException(string.Join(" ", validation.Errors.Select(error => error.Message)));
        return environment with { Name = name, Description = description, ConfigurationJson = JsonSerializer.Serialize(configuration, JsonOptions) };
    }
    private static EnvironmentResponse Response(ExecutionEnvironmentProfile environment) => new(environment.Id, environment.Revision,
        environment.Name, environment.Description, JsonSerializer.Deserialize<EnvironmentConfiguration>(environment.ConfigurationJson, JsonOptions)!,
        false, environment.CreatedAt, environment.UpdatedAt);
    private static void EnsureMutable(string id)
    { if (id == DefaultEnvironmentId) throw new ArgumentException("The default environment cannot be edited or deleted."); }
    private static void EnsureRevision(int revision)
    { if (revision < 1) throw new ArgumentException("Expected revision must be positive."); }
    private static EnvironmentConflictException Conflict() => new("This environment changed. Reload its current revision before retrying.");
}
