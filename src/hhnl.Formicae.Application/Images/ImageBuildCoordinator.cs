using hhnl.Formicae.Application.Workflows;
using Microsoft.Extensions.Logging;

namespace hhnl.Formicae.Application.Images;

public sealed class ImageBuildCoordinator(IImageStore store, IImageBuildRuntime runtime, IClock clock, ILogger<ImageBuildCoordinator>? logger = null)
{
    public async Task TickAsync(int concurrency, TimeSpan deadline, CancellationToken token)
    {
        var builds = await store.BuildsAsync(null, token);
        foreach (var build in builds.Where(x => ImageJson.Terminal(x.State) && x.CleanupPending))
        {
            await runtime.CleanupAsync(build, token);
            await store.UpdateBuildAsync(build with { CleanupPending = false, Revision = build.Revision + 1 }, build.Revision, token);
        }
        var active = builds.Where(x => !ImageJson.Terminal(x.State)).OrderBy(x => x.State == ImageBuildState.Queued).ThenBy(x => x.CreatedAt).Take(Math.Clamp(concurrency, 1, 16));
        foreach (var original in active)
        {
            var build = original;
            if (build.StartedAt is null)
            {
                build = build with { StartedAt = clock.UtcNow, Revision = build.Revision + 1 };
                if (!await store.UpdateBuildAsync(build, original.Revision, token)) continue;
            }
            ImageBuildObservation observation;
            if (clock.UtcNow - build.StartedAt > deadline)
                observation = new(ImageBuildState.TimedOut, build.Logs, Failure: "Image build exceeded its configured deadline.");
            else
            {
                try { observation = await runtime.ReconcileAsync(build, token); }
                catch (OperationCanceledException) when(token.IsCancellationRequested) { throw; }
                catch (Exception exception) {
                    logger?.LogError(exception, "Image build infrastructure failed for build {BuildId}", build.Id);
                    observation = new(ImageBuildState.Failed, build.Logs, Failure: "Image build infrastructure failed. Check operator logs and registry configuration.");
                }
            }
            if (observation.State == ImageBuildState.Ready && (observation.Reference is null || !ImageService.IsDigest(observation.Reference)))
                observation = observation with { State = ImageBuildState.Failed, Failure = "The build did not produce a verified image digest." };
            var logs = observation.Logs.Length > 65536 ? observation.Logs[^65536..] : observation.Logs;
            var next = build with { State = observation.State, Reference = observation.Reference ?? build.Reference, Logs = logs,
                Failure = observation.Failure, CleanupPending = ImageJson.Terminal(observation.State), Revision = build.Revision + 1, UpdatedAt = clock.UtcNow };
            // Cancellation wins over promotion: a stale observation can never overwrite its newer revision.
            if (!await store.UpdateBuildAsync(next, build.Revision, token))
                await runtime.CleanupAsync(build, token);
        }
    }
}
