using hhnl.Formicae.Application.Workflows;
using Microsoft.EntityFrameworkCore;

namespace hhnl.Formicae.Infrastructure.Persistence;

public sealed class EfAiSettingsStore(FormicaeDbContext dbContext) : IAiSettingsStore
{
    public Task<AiSettings?> GetAsync(CancellationToken cancellationToken)
        => dbContext.AiSettings
            .OrderBy(settings => settings.CreatedAt)
            .ThenBy(settings => settings.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<AiSettings?> GetAsync(string id, CancellationToken cancellationToken)
        => dbContext.AiSettings.SingleOrDefaultAsync(settings => settings.Id == id, cancellationToken);

    public async Task<IReadOnlyList<AiSettings>> ListAsync(CancellationToken cancellationToken)
        => await dbContext.AiSettings
            .OrderBy(settings => settings.CreatedAt)
            .ThenBy(settings => settings.Id)
            .ToListAsync(cancellationToken);

    public async Task<AiSettings> UpsertAsync(AiSettings settings, CancellationToken cancellationToken)
    {
        var exists = await dbContext.AiSettings.AnyAsync(existing => existing.Id == settings.Id, cancellationToken);
        if (exists)
        {
            dbContext.AiSettings.Update(settings);
        }
        else
        {
            dbContext.AiSettings.Add(settings);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }
}