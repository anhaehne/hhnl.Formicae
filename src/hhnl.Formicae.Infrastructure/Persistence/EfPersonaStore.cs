using hhnl.Formicae.Application.Workflows;
using Microsoft.EntityFrameworkCore;

namespace hhnl.Formicae.Infrastructure.Persistence;

public sealed class EfPersonaStore(FormicaeDbContext db) : IPersonaStore
{
    public Task<Persona?> GetAsync(string id, CancellationToken token)
        => db.Personas.AsNoTracking().SingleOrDefaultAsync(persona => persona.Id == id && !persona.IsDeleted, token);
    public async Task<IReadOnlyList<Persona>> ListAsync(CancellationToken token)
        => await db.Personas.AsNoTracking().Where(persona => !persona.IsDeleted).OrderBy(persona => persona.Id).ToListAsync(token);
    public async Task<Persona> CreateAsync(Persona persona, CancellationToken token)
    { db.Personas.Add(persona); await db.SaveChangesAsync(token); return persona; }
    public async Task<bool> TryUpdateAsync(Persona replacement, int expectedRevision, CancellationToken token)
    {
        if (replacement.Revision != checked(expectedRevision + 1)) throw new ArgumentException("Replacement revision must advance exactly once.");
        return await db.Personas.Where(persona => persona.Id == replacement.Id && !persona.IsDeleted && persona.Revision == expectedRevision)
            .ExecuteUpdateAsync(setters => setters.SetProperty(persona => persona.Name, replacement.Name)
                .SetProperty(persona => persona.Instructions, replacement.Instructions).SetProperty(persona => persona.Tone, replacement.Tone)
                .SetProperty(persona => persona.OperatingConstraints, replacement.OperatingConstraints).SetProperty(persona => persona.Revision, replacement.Revision)
                .SetProperty(persona => persona.IsDeleted, replacement.IsDeleted).SetProperty(persona => persona.UpdatedAt, replacement.UpdatedAt), token) == 1;
    }
}
