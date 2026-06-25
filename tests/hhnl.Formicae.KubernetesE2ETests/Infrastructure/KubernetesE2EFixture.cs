using System.Diagnostics;
using System.Net.Sockets;

namespace hhnl.Formicae.KubernetesE2ETests.Infrastructure;

public sealed class KubernetesE2EFixture : IAsyncLifetime
{
    private const string ClusterName = "formicae-e2e";
    private const string Namespace = "formicae";
    private const string ApiImage = "localhost/hhnl-formicae-api:e2e";

    private readonly List<Process> longRunningProcesses = [];
    private bool ownsCluster;

    public string RepositoryRoot { get; } = FindRepositoryRoot();
    public string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "formicae-e2e");
    public string KubeconfigPath => Path.Combine(TempRoot, "kubeconfig");
    public string ContainerCli => Environment.GetEnvironmentVariable("FORMICAE_CONTAINER_CLI") switch
    {
        { Length: > 0 } value => value,
        _ => "docker"
    };

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(TempRoot);
        await PreflightAsync();
        await EnsureClusterAsync();
        await BuildAndLoadImagesAsync();
        await DeployAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var process in longRunningProcesses)
        {
            CommandRunner.TryKill(process);
            process.Dispose();
        }

        if (string.Equals(Environment.GetEnvironmentVariable("FORMICAE_E2E_KEEP_CLUSTER"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (ownsCluster)
        {
            await CommandRunner.RunAsync("kind", ["delete", "cluster", "--name", ClusterName, "--kubeconfig", KubeconfigPath], RepositoryRoot, TimeSpan.FromMinutes(2), KindEnvironment());
        }
        else if (File.Exists(KubeconfigPath))
        {
            await KubectlAsync(["delete", "namespace", Namespace, "--ignore-not-found=true"], TimeSpan.FromMinutes(2));
        }
    }

    public async Task<PortForwardHandle> StartApiPortForwardAsync()
    {
        var port = GetFreeTcpPort();
        var process = CommandRunner.StartLongRunning(
            "kubectl",
            KubectlArgs(["port-forward", "service/formicae-api", $"{port}:80", "-n", Namespace]),
            RepositoryRoot);
        longRunningProcesses.Add(process);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"kubectl port-forward exited early with code {process.ExitCode}.");
            }

            if (await CanConnectAsync(port))
            {
                return new PortForwardHandle(port, process, longRunningProcesses);
            }

            await Task.Delay(250);
        }

        CommandRunner.TryKill(process);
        throw new TimeoutException("Timed out waiting for kubectl port-forward to become ready.");
    }

    public async Task RestartApiAsync()
    {
        await KubectlRequiredAsync(["rollout", "restart", "deployment/formicae-api", "-n", Namespace], TimeSpan.FromMinutes(1));
        await KubectlRequiredAsync(["rollout", "status", "deployment/formicae-api", "-n", Namespace, "--timeout=180s"], TimeSpan.FromMinutes(4));
    }

    public async Task<string> CaptureDiagnosticsAsync()
    {
        var sections = new List<string>();
        await AddDiagnosticAsync(sections, "kubectl get all", ["get", "all", "-n", Namespace, "-o", "wide"]);
        await AddDiagnosticAsync(sections, "kubectl describe pods", ["describe", "pods", "-n", Namespace]);
        await AddDiagnosticAsync(sections, "api logs", ["logs", "deployment/formicae-api", "-n", Namespace, "--tail=200"]);
        await AddDiagnosticAsync(sections, "postgres logs", ["logs", "deployment/formicae-postgres", "-n", Namespace, "--tail=200"]);
        return string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    public async Task<string> RenderE2EOverlayAsync()
        => (await CommandRunner.RunRequiredAsync("kubectl", ["kustomize", "deploy/kubernetes/overlays/e2e"], RepositoryRoot, TimeSpan.FromSeconds(30))).StandardOutput;

    private async Task PreflightAsync()
    {
        await RequireToolAsync("kind", ["version"], "Install kind and ensure it is on PATH.");
        await RequireToolAsync("kubectl", ["version", "--client"], "Install kubectl and ensure it is on PATH.");
        await RequireToolAsync(ContainerCli, ["--version"], $"Install {ContainerCli} or set FORMICAE_CONTAINER_CLI=docker|podman.");
    }

    private async Task RequireToolAsync(string fileName, string[] args, string installHint)
    {
        try
        {
            await CommandRunner.RunRequiredAsync(fileName, args, RepositoryRoot, TimeSpan.FromSeconds(30), fileName == "kind" ? KindEnvironment() : null);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Required tool '{fileName}' is not available. {installHint}", exception);
        }
    }

    private async Task EnsureClusterAsync()
    {
        var clusters = await CommandRunner.RunRequiredAsync("kind", ["get", "clusters"], RepositoryRoot, TimeSpan.FromSeconds(30), KindEnvironment());
        var exists = clusters.StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Contains(ClusterName, StringComparer.OrdinalIgnoreCase);

        if (exists)
        {
            ownsCluster = false;
            await CommandRunner.RunRequiredAsync("kind", ["export", "kubeconfig", "--name", ClusterName, "--kubeconfig", KubeconfigPath], RepositoryRoot, TimeSpan.FromSeconds(30), KindEnvironment());
            await KubectlAsync(["delete", "namespace", Namespace, "--ignore-not-found=true"], TimeSpan.FromMinutes(2));
            return;
        }

        ownsCluster = true;
        await CommandRunner.RunRequiredAsync("kind", ["create", "cluster", "--name", ClusterName, "--kubeconfig", KubeconfigPath, "--wait", "5m"], RepositoryRoot, TimeSpan.FromMinutes(6), KindEnvironment());
    }

    private async Task BuildAndLoadImagesAsync()
    {
        await CommandRunner.RunRequiredAsync(ContainerCli, ["build", "-f", "src/hhnl.Formicae.Api/Dockerfile", "-t", ApiImage, "."], RepositoryRoot, TimeSpan.FromMinutes(5));

        var apiArchive = Path.Combine(TempRoot, "formicae-api-e2e.tar");
        File.Delete(apiArchive);

        await CommandRunner.RunRequiredAsync(ContainerCli, ["save", "-o", apiArchive, ApiImage], RepositoryRoot, TimeSpan.FromMinutes(3));
        await CommandRunner.RunRequiredAsync("kind", ["load", "image-archive", apiArchive, "--name", ClusterName], RepositoryRoot, TimeSpan.FromMinutes(3), KindEnvironment());
    }

    private async Task DeployAsync()
    {
        await KubectlRequiredAsync(["apply", "-k", "deploy/kubernetes/overlays/e2e"], TimeSpan.FromMinutes(2));
        await KubectlRequiredAsync(["rollout", "status", "deployment/formicae-postgres", "-n", Namespace, "--timeout=180s"], TimeSpan.FromMinutes(4));
        await KubectlRequiredAsync(["rollout", "status", "deployment/formicae-api", "-n", Namespace, "--timeout=180s"], TimeSpan.FromMinutes(4));
    }

    private async Task AddDiagnosticAsync(List<string> sections, string title, string[] args)
    {
        try
        {
            var result = await KubectlAsync(args, TimeSpan.FromSeconds(30));
            sections.Add($"## {title}{Environment.NewLine}{result.CombinedOutput}");
        }
        catch (Exception exception)
        {
            sections.Add($"## {title}{Environment.NewLine}{exception.Message}");
        }
    }

    private Task<CommandResult> KubectlAsync(string[] args, TimeSpan timeout)
        => CommandRunner.RunAsync("kubectl", KubectlArgs(args), RepositoryRoot, timeout);

    private async Task KubectlRequiredAsync(string[] args, TimeSpan timeout)
    {
        var result = await KubectlAsync(args, timeout);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"kubectl command failed: {result.CombinedOutput}");
        }
    }

    private string[] KubectlArgs(string[] args)
        => ["--kubeconfig", KubeconfigPath, .. args];

    private IReadOnlyDictionary<string, string?>? KindEnvironment()
        => string.Equals(ContainerCli, "podman", StringComparison.OrdinalIgnoreCase)
            ? new Dictionary<string, string?> { ["KIND_EXPERIMENTAL_PROVIDER"] = "podman" }
            : null;

    private static async Task<bool> CanConnectAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromMilliseconds(500));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "hhnl.Formicae.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }
}

public sealed class PortForwardHandle(int port, Process process, List<Process> ownerProcesses) : IDisposable
{
    public Uri BaseAddress { get; } = new($"http://127.0.0.1:{port}");

    public void Dispose()
    {
        CommandRunner.TryKill(process);
        ownerProcesses.Remove(process);
        process.Dispose();
    }
}
