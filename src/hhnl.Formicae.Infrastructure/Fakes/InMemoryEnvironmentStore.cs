using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Infrastructure.Fakes;

public sealed class InMemoryEnvironmentStore : IEnvironmentStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, ExecutionEnvironmentProfile> environments = new(StringComparer.Ordinal);
    public Task<ExecutionEnvironmentProfile?> GetAsync(string id, CancellationToken token)
    {
        lock (gate) return Task.FromResult(environments.GetValueOrDefault(id) is { IsDeleted: false } environment ? environment : null);
    }
    public Task<IReadOnlyList<ExecutionEnvironmentProfile>> ListAsync(CancellationToken token)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<ExecutionEnvironmentProfile>>(environments.Values.Where(environment => !environment.IsDeleted).OrderBy(environment => environment.Id).ToArray());
    }
    public Task<ExecutionEnvironmentProfile> CreateAsync(ExecutionEnvironmentProfile environment, CancellationToken token)
    { lock (gate) environments.Add(environment.Id, environment); return Task.FromResult(environment); }
    public Task<bool> TryUpdateAsync(ExecutionEnvironmentProfile replacement, int expectedRevision, CancellationToken token)
    {
        if (replacement.Revision != checked(expectedRevision + 1)) throw new ArgumentException("Replacement revision must advance exactly once.");
        lock (gate)
        {
            if (!environments.TryGetValue(replacement.Id, out var existing) || existing.IsDeleted || existing.Revision != expectedRevision) return Task.FromResult(false);
            environments[replacement.Id] = replacement with { CreatedAt = existing.CreatedAt };
            return Task.FromResult(true);
        }
    }
}
