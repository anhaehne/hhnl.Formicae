using hhnl.Formicae.Application.Workflows;
using Microsoft.EntityFrameworkCore;

namespace hhnl.Formicae.Infrastructure.Persistence;

public sealed class EfEnvironmentStore(FormicaeDbContext db) : IEnvironmentStore
{
    public Task<ExecutionEnvironmentProfile?> GetAsync(string id, CancellationToken token)
        => db.ExecutionEnvironments.AsNoTracking().SingleOrDefaultAsync(environment => environment.Id == id && !environment.IsDeleted, token);
    public async Task<IReadOnlyList<ExecutionEnvironmentProfile>> ListAsync(CancellationToken token)
        => await db.ExecutionEnvironments.AsNoTracking().Where(environment => !environment.IsDeleted).OrderBy(environment => environment.Id).ToListAsync(token);
    public async Task<ExecutionEnvironmentProfile> CreateAsync(ExecutionEnvironmentProfile environment, CancellationToken token)
    { db.ExecutionEnvironments.Add(environment); await db.SaveChangesAsync(token); return environment; }
    public async Task<bool> TryUpdateAsync(ExecutionEnvironmentProfile replacement, int expectedRevision, CancellationToken token)
    {
        if (replacement.Revision != checked(expectedRevision + 1)) throw new ArgumentException("Replacement revision must advance exactly once.");
        return await db.ExecutionEnvironments.Where(environment => environment.Id == replacement.Id && !environment.IsDeleted && environment.Revision == expectedRevision)
            .ExecuteUpdateAsync(setters => setters.SetProperty(environment => environment.Name, replacement.Name)
                .SetProperty(environment => environment.Description, replacement.Description).SetProperty(environment => environment.ConfigurationJson, replacement.ConfigurationJson)
                .SetProperty(environment => environment.Revision, replacement.Revision).SetProperty(environment => environment.IsDeleted, replacement.IsDeleted)
                .SetProperty(environment => environment.UpdatedAt, replacement.UpdatedAt), token) == 1;
    }
}
