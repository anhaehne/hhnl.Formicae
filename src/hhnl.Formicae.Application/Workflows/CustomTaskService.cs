using System.Text.Json;

namespace hhnl.Formicae.Application.Workflows;

public sealed class CustomTaskService(ICustomTaskStore store, IClock? clock = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IClock clock = clock ?? new SystemClock();

    public async Task<CustomTaskResponse?> GetAsync(string id, CancellationToken token)
        => (await store.GetAsync(id, token)) is { } task ? Response(task) : null;
    public async Task<IReadOnlyList<CustomTaskResponse>> ListAsync(CancellationToken token)
        => (await store.ListAsync(token)).OrderBy(task => task.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(task => task.Id, StringComparer.Ordinal).Select(Response).ToArray();
    public async Task<CustomTaskResponse> CreateAsync(CreateCustomTaskRequest request, CancellationToken token)
    {
        var now = clock.UtcNow;
        var task = Normalize(new CustomTaskDefinition { Id = Guid.NewGuid().ToString("N"), Name = request.Name,
            PromptTemplate = request.PromptTemplate, CreatedAt = now, UpdatedAt = now },
            request.Name, request.Description, request.PromptTemplate, request.Inputs, request.Runner);
        return Response(await store.CreateAsync(task, token));
    }
    public async Task<CustomTaskResponse?> UpdateAsync(string id, UpdateCustomTaskRequest request, CancellationToken token)
    {
        EnsureRevision(request.ExpectedRevision);
        var existing = await store.GetAsync(id, token);
        if (existing is null) return null;
        if (existing.Revision != request.ExpectedRevision) throw Conflict();
        var replacement = Normalize(existing with { Revision = checked(existing.Revision + 1), UpdatedAt = clock.UtcNow },
            request.Name, request.Description, request.PromptTemplate, request.Inputs, request.Runner);
        if (!await store.TryUpdateAsync(replacement, request.ExpectedRevision, token)) throw Conflict();
        return Response(replacement);
    }
    public async Task<bool> DeleteAsync(string id, int expectedRevision, CancellationToken token)
    {
        EnsureRevision(expectedRevision);
        var existing = await store.GetAsync(id, token);
        if (existing is null) return false;
        if (existing.Revision != expectedRevision) throw Conflict();
        if (!await store.TryUpdateAsync(existing with { IsDeleted = true, Revision = checked(existing.Revision + 1), UpdatedAt = clock.UtcNow }, expectedRevision, token)) throw Conflict();
        return true;
    }
    private static CustomTaskDefinition Normalize(CustomTaskDefinition task, string? name, string? description, string? prompt,
        IReadOnlyList<CustomTaskInputDefinition>? inputs, CustomTaskRunnerSettings? runner)
    {
        name = name?.Trim() ?? ""; description = description?.Trim() ?? "";
        inputs ??= []; runner ??= new();
        var validation = CustomTaskDefinitions.ValidateCatalog(name, description, prompt, inputs, runner);
        if (!validation.IsValid) throw new ArgumentException(string.Join(" ", validation.Errors.Select(error => error.Message)));
        return task with { Name = name, Description = description, PromptTemplate = prompt!,
            InputsJson = JsonSerializer.Serialize(inputs, JsonOptions), RunnerJson = JsonSerializer.Serialize(runner, JsonOptions) };
    }
    private static CustomTaskResponse Response(CustomTaskDefinition task) => new(task.Id, task.Revision, task.Name, task.Description,
        task.PromptTemplate, JsonSerializer.Deserialize<CustomTaskInputDefinition[]>(task.InputsJson, JsonOptions)!,
        JsonSerializer.Deserialize<CustomTaskRunnerSettings>(task.RunnerJson, JsonOptions)!, task.CreatedAt, task.UpdatedAt);
    private static void EnsureRevision(int revision)
    { if (revision < 1) throw new ArgumentException("Expected revision must be positive."); }
    private static CustomTaskConflictException Conflict() => new("This task changed. Reload its current revision before retrying.");
}
