using hhnl.Formicae.Application.Workflows;
using Microsoft.EntityFrameworkCore;

namespace hhnl.Formicae.Infrastructure.Persistence;

public sealed class EfAiSettingsStore(FormicaeDbContext dbContext) : IAiSettingsStore
{
    public Task<AiSettings?> GetAsync(CancellationToken cancellationToken)
        => dbContext.AiSettings.SingleOrDefaultAsync(settings => settings.Id == AiSettings.DefaultId, cancellationToken);

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
