using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Infrastructure.Fakes;

public sealed class InMemoryPersonaStore : IPersonaStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, Persona> personas = new(StringComparer.Ordinal);
    public Task<Persona?> GetAsync(string id, CancellationToken token)
    {
        lock (gate) return Task.FromResult(personas.GetValueOrDefault(id) is { IsDeleted: false } persona ? persona : null);
    }
    public Task<IReadOnlyList<Persona>> ListAsync(CancellationToken token)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<Persona>>(personas.Values.Where(persona => !persona.IsDeleted).OrderBy(persona => persona.Id).ToArray());
    }
    public Task<Persona> CreateAsync(Persona persona, CancellationToken token)
    { lock (gate) personas.Add(persona.Id, persona); return Task.FromResult(persona); }
    public Task<bool> TryUpdateAsync(Persona replacement, int expectedRevision, CancellationToken token)
    {
        if (replacement.Revision != checked(expectedRevision + 1)) throw new ArgumentException("Replacement revision must advance exactly once.");
        lock (gate)
        {
            if (!personas.TryGetValue(replacement.Id, out var existing) || existing.IsDeleted || existing.Revision != expectedRevision) return Task.FromResult(false);
            personas[replacement.Id] = replacement with { CreatedAt = existing.CreatedAt };
            return Task.FromResult(true);
        }
    }
}
