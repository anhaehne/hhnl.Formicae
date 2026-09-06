using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Infrastructure.Fakes;

public sealed class InMemoryCustomTaskStore : ICustomTaskStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, CustomTaskDefinition> tasks = new(StringComparer.Ordinal);
    public Task<CustomTaskDefinition?> GetAsync(string id, CancellationToken token)
    {
        lock (gate) return Task.FromResult(tasks.GetValueOrDefault(id) is { IsDeleted: false } task ? task : null);
    }
    public Task<IReadOnlyList<CustomTaskDefinition>> ListAsync(CancellationToken token)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<CustomTaskDefinition>>(tasks.Values.Where(task => !task.IsDeleted).OrderBy(task => task.Id).ToArray());
    }
    public Task<CustomTaskDefinition> CreateAsync(CustomTaskDefinition task, CancellationToken token)
    { lock (gate) tasks.Add(task.Id, task); return Task.FromResult(task); }
    public Task<bool> TryUpdateAsync(CustomTaskDefinition replacement, int expectedRevision, CancellationToken token)
    {
        if (replacement.Revision != checked(expectedRevision + 1)) throw new ArgumentException("Replacement revision must advance exactly once.");
        lock (gate)
        {
            if (!tasks.TryGetValue(replacement.Id, out var existing) || existing.IsDeleted || existing.Revision != expectedRevision) return Task.FromResult(false);
            tasks[replacement.Id] = replacement with { CreatedAt = existing.CreatedAt };
            return Task.FromResult(true);
        }
    }
}
