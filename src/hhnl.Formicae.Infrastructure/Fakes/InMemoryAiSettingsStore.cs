using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Infrastructure.Fakes;

public sealed class InMemoryAiSettingsStore : IAiSettingsStore
{
    private readonly object gate = new();
    private AiSettings? settings;

    public Task<AiSettings?> GetAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(settings);
        }
    }

    public Task<AiSettings> UpsertAsync(AiSettings settings, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            this.settings = settings;
        }

        return Task.FromResult(settings);
    }
}
