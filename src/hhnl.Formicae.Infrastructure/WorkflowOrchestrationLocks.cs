using hhnl.Formicae.Application.Workflows;
using Medallion.Threading.Postgres;
using Microsoft.Extensions.Configuration;

namespace hhnl.Formicae.Infrastructure;

public sealed class InMemoryWorkflowOrchestrationLock : IWorkflowOrchestrationLock
{
    private readonly SemaphoreSlim semaphore = new(1, 1);

    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        return await semaphore.WaitAsync(TimeSpan.Zero, cancellationToken)
            ? new Releaser(semaphore)
            : null;
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class PostgresWorkflowOrchestrationLock : IWorkflowOrchestrationLock
{
    private readonly PostgresDistributedLock distributedLock;

    public PostgresWorkflowOrchestrationLock(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Formicae")
            ?? throw new InvalidOperationException("ConnectionStrings:Formicae is required for PostgreSQL workflow orchestration locking.");
        distributedLock = new PostgresDistributedLock(new PostgresAdvisoryLockKey("formicae:workflow-orchestration", allowHashing: true), connectionString);
    }

    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
        => await distributedLock.TryAcquireAsync(TimeSpan.Zero, cancellationToken);
}