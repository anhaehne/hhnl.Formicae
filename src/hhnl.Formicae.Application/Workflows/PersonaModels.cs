namespace hhnl.Formicae.Application.Workflows;

public sealed record Persona
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Instructions { get; init; } = "";
    public string Tone { get; init; } = "";
    public string OperatingConstraints { get; init; } = "";
    public int Revision { get; init; } = 1;
    public bool IsDeleted { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record PersonaSnapshot(string Id, int Revision, string Name, string Instructions, string Tone, string OperatingConstraints);
public sealed record PersonaResponse(string Id, string Name, string Instructions, string Tone, string OperatingConstraints,
    int Revision, bool BuiltIn, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record CreatePersonaRequest(string Name, string? Instructions = null, string? Tone = null, string? OperatingConstraints = null);
public sealed record UpdatePersonaRequest(int ExpectedRevision, string Name, string? Instructions = null, string? Tone = null, string? OperatingConstraints = null);
public sealed class PersonaConflictException(string message) : Exception(message);

public interface IPersonaStore
{
    Task<Persona?> GetAsync(string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<Persona>> ListAsync(CancellationToken cancellationToken);
    Task<Persona> CreateAsync(Persona persona, CancellationToken cancellationToken);
    Task<bool> TryUpdateAsync(Persona replacement, int expectedRevision, CancellationToken cancellationToken);
}
