using hhnl.Formicae.Infrastructure;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowOrchestrationLockTests
{
    [Fact]
    public async Task InMemory_lock_allows_single_holder_at_a_time()
    {
        var orchestrationLock = new InMemoryWorkflowOrchestrationLock();

        var firstHandle = Assert.IsAssignableFrom<IAsyncDisposable>(await orchestrationLock.TryAcquireAsync(CancellationToken.None));
        await using var secondHandle = await orchestrationLock.TryAcquireAsync(CancellationToken.None);

        Assert.Null(secondHandle);

        await firstHandle.DisposeAsync();
        var thirdHandle = Assert.IsAssignableFrom<IAsyncDisposable>(await orchestrationLock.TryAcquireAsync(CancellationToken.None));
        await thirdHandle.DisposeAsync();
    }
}
