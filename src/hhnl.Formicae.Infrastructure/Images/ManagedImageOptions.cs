namespace hhnl.Formicae.Infrastructure.Images;

public sealed class ManagedImageOptions
{
    public bool Enabled { get; set; }
    public bool BundledRegistry { get; set; } = true;
    public string Registry { get; set; } = "";
    public string RepositoryPrefix { get; set; } = "formicae/local";
    public string WorkerBaseImage { get; set; } = "";
    public string BuilderImage { get; set; } = "moby/buildkit:v0.28.0-rootless";
    public string BuildNamespace { get; set; } = "";
    public string BuildServiceAccount { get; set; } = "";
    public string[] ExternalTokenHosts { get; set; } = [];
    public string PushSecretName { get; set; } = "";
    public string[] PullSecretNames { get; set; } = [];
    public string[] NodeSelector { get; set; } = [];
    public int Concurrency { get; set; } = 2;
    public int TimeoutSeconds { get; set; } = 1200;
    public string TokenIssuer { get; set; } = "formicae-images";
    public string TokenService { get; set; } = "formicae-registry";
    public string SigningCertificatePath { get; set; } = "";
    public string SigningKeyPath { get; set; } = "";
    public string PullPassword { get; set; } = "";
    public bool AllowInsecureRegistryForTests { get; set; }
}
