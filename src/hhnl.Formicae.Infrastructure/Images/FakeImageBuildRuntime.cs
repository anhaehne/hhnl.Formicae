using System.Security.Cryptography;
using System.Text;
using hhnl.Formicae.Application.Images;

namespace hhnl.Formicae.Infrastructure.Images;

/// <summary>Deterministic dev/smoke adapter; production always uses real Kubernetes builds.</summary>
public sealed class FakeImageBuildRuntime : IImageBuildRuntime
{
    public Task<ImageBuildObservation> ReconcileAsync(ImageBuild build, CancellationToken token)
    {
        var source=ImageJson.Source(build.SourceJson);
        var result=source.Dockerfile.Contains("FAIL_BUILD",StringComparison.Ordinal)
            ? new ImageBuildObservation(ImageBuildState.Failed,"Simulated Dockerfile error for the development adapter.",Failure:"Development build failed.")
            : build.State==ImageBuildState.Queued
            ? new(ImageBuildState.Building,"Development adapter: building image.")
            : build.State==ImageBuildState.Building
            ? new(ImageBuildState.Validating,"Development adapter: checking worker compatibility.","registry.example/formicae/local/"+build.ImageId+"@sha256:"+Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(build.SourceJson))))
            : new(ImageBuildState.Ready,"Development adapter: worker compatibility passed.",build.Reference);
        return Task.FromResult(result);
    }
    public Task CleanupAsync(ImageBuild build,CancellationToken token)=>Task.CompletedTask;
}

public sealed class DisabledImageBuildRuntime : IImageBuildRuntime
{
    public Task<ImageBuildObservation> ReconcileAsync(ImageBuild build,CancellationToken token)=>Task.FromResult(new ImageBuildObservation(ImageBuildState.Failed,Failure:"Managed image builds are disabled by the operator."));
    public Task CleanupAsync(ImageBuild build,CancellationToken token)=>Task.CompletedTask;
}
