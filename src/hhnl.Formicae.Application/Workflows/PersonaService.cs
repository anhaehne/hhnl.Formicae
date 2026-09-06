namespace hhnl.Formicae.Application.Workflows;

public sealed class PersonaService(IPersonaStore store, IClock? clock = null)
{
    public const string DefaultPersonaId = "default";
    public static PersonaSnapshot DefaultSnapshot { get; } = new(DefaultPersonaId, 1, "Default behavior", "", "", "");
    private static PersonaResponse DefaultResponse => new(DefaultPersonaId, DefaultSnapshot.Name, "", "", "", 1, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    private readonly IClock clock = clock ?? new SystemClock();

    public async Task<PersonaResponse?> GetAsync(string id, CancellationToken token)
        => id == DefaultPersonaId ? DefaultResponse : (await store.GetAsync(id, token)) is { } persona ? Response(persona) : null;
    public async Task<IReadOnlyList<PersonaResponse>> ListAsync(CancellationToken token)
        => new[] { DefaultResponse }.Concat((await store.ListAsync(token)).Where(persona => persona.Id != DefaultPersonaId)
            .OrderBy(persona => persona.Name, StringComparer.OrdinalIgnoreCase).ThenBy(persona => persona.Id).Select(Response)).ToArray();
    public async Task<PersonaResponse> CreateAsync(CreatePersonaRequest request, CancellationToken token)
    {
        var now = clock.UtcNow;
        var persona = Normalize(new Persona { Id = Guid.NewGuid().ToString("N"), Name = request.Name,
            CreatedAt = now, UpdatedAt = now }, request.Name, request.Instructions, request.Tone, request.OperatingConstraints);
        return Response(await store.CreateAsync(persona, token));
    }
    public async Task<PersonaResponse?> UpdateAsync(string id, UpdatePersonaRequest request, CancellationToken token)
    {
        EnsureMutable(id); EnsureRevision(request.ExpectedRevision);
        var existing = await store.GetAsync(id, token);
        if (existing is null) return null;
        if (existing.Revision != request.ExpectedRevision) throw Conflict();
        var replacement = Normalize(existing with { Revision = checked(existing.Revision + 1), UpdatedAt = clock.UtcNow },
            request.Name, request.Instructions, request.Tone, request.OperatingConstraints);
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
    private static Persona Normalize(Persona persona, string? name, string? instructions, string? tone, string? constraints)
    {
        static string Field(string? value, string label, int limit, bool required = false)
        {
            var normalized = value?.Trim() ?? "";
            if ((required && normalized.Length == 0) || normalized.Length > limit)
                throw new ArgumentException($"{label} {(required ? "is required and " : "")}must contain at most {limit} characters.");
            return normalized;
        }
        return persona with { Name = Field(name, "Name", 120, true), Instructions = Field(instructions, "Instructions", 16000),
            Tone = Field(tone, "Tone", 1000), OperatingConstraints = Field(constraints, "Operating constraints", 8000) };
    }
    private static void EnsureMutable(string id)
    { if (id == DefaultPersonaId) throw new ArgumentException("Default behavior cannot be edited or deleted."); }
    private static void EnsureRevision(int revision)
    { if (revision < 1) throw new ArgumentException("Expected revision must be positive."); }
    private static PersonaConflictException Conflict() => new("This persona changed. Reload its current revision before retrying.");
    private static PersonaResponse Response(Persona persona) => new(persona.Id, persona.Name, persona.Instructions, persona.Tone,
        persona.OperatingConstraints, persona.Revision, false, persona.CreatedAt, persona.UpdatedAt);
}
