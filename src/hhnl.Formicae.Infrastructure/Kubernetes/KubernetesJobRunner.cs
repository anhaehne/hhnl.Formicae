namespace hhnl.Formicae.Infrastructure.Kubernetes;

public interface IKubernetesJobRunner
{
    Task<KubernetesJobResult> RunJobAsync(KubernetesJobSpec spec, CancellationToken cancellationToken);
}

public sealed record KubernetesJobSpec(string Name, string Image, IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> Command);
public sealed record KubernetesJobResult(bool Succeeded, string JobName, string Logs, string? FailureReason);

public sealed class KubernetesJobOptions
{
    public string Namespace { get; set; } = "default";
    public string Image { get; set; } = "ghcr.io/openhands/openhands:latest";
    public string WorkspaceVolumeClaim { get; set; } = "formicae-workspaces";
}

public sealed class KubernetesJobRunner : IKubernetesJobRunner
{
    public Task<KubernetesJobResult> RunJobAsync(KubernetesJobSpec spec, CancellationToken cancellationToken)
    {
        var manifest = KubernetesJobManifest.Render(spec);
        return Task.FromResult(new KubernetesJobResult(true, spec.Name, manifest, null));
    }
}
