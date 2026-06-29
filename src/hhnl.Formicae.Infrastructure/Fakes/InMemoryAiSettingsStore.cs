using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Infrastructure.Fakes;

public sealed class InMemoryAiSettingsStore : IAiSettingsStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, AiSettings> settings = [];

    public Task<AiSettings?> GetAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(settings.Values.OrderBy(setting => setting.CreatedAt).ThenBy(setting => setting.Id).FirstOrDefault());
        }
    }

    public Task<AiSettings?> GetAsync(string id, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(settings.GetValueOrDefault(id));
        }
    }

    public Task<IReadOnlyList<AiSettings>> ListAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<AiSettings>>(settings.Values.OrderBy(setting => setting.CreatedAt).ThenBy(setting => setting.Id).ToList());
        }
    }

    public Task<AiSettings> UpsertAsync(AiSettings setting, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            settings[setting.Id] = setting;
        }

        return Task.FromResult(setting);
    }
}