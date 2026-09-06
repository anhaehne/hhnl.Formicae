using hhnl.Formicae.Application.Workflows;
using Microsoft.EntityFrameworkCore;

namespace hhnl.Formicae.Infrastructure.Persistence;

public sealed class EfCustomTaskStore(FormicaeDbContext db) : ICustomTaskStore
{
    public Task<CustomTaskDefinition?> GetAsync(string id, CancellationToken token)
        => db.CustomTasks.AsNoTracking().SingleOrDefaultAsync(task => task.Id == id && !task.IsDeleted, token);
    public async Task<IReadOnlyList<CustomTaskDefinition>> ListAsync(CancellationToken token)
        => await db.CustomTasks.AsNoTracking().Where(task => !task.IsDeleted).OrderBy(task => task.Id).ToListAsync(token);
    public async Task<CustomTaskDefinition> CreateAsync(CustomTaskDefinition task, CancellationToken token)
    { db.CustomTasks.Add(task); await db.SaveChangesAsync(token); return task; }
    public async Task<bool> TryUpdateAsync(CustomTaskDefinition replacement, int expectedRevision, CancellationToken token)
    {
        if (replacement.Revision != checked(expectedRevision + 1)) throw new ArgumentException("Replacement revision must advance exactly once.");
        return await db.CustomTasks.Where(task => task.Id == replacement.Id && !task.IsDeleted && task.Revision == expectedRevision)
            .ExecuteUpdateAsync(setters => setters.SetProperty(task => task.Name, replacement.Name)
                .SetProperty(task => task.Description, replacement.Description).SetProperty(task => task.PromptTemplate, replacement.PromptTemplate)
                .SetProperty(task => task.InputsJson, replacement.InputsJson).SetProperty(task => task.RunnerJson, replacement.RunnerJson)
                .SetProperty(task => task.Revision, replacement.Revision).SetProperty(task => task.IsDeleted, replacement.IsDeleted)
                .SetProperty(task => task.UpdatedAt, replacement.UpdatedAt), token) == 1;
    }
}
