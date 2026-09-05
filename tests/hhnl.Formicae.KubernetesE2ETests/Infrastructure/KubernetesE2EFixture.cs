using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

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
        try
        {
            await EnsureClusterAsync();
            await BuildAndLoadImagesAsync();
            await DeployAsync();
        }
        catch (Exception exception)
        {
            string diagnostics;
            try { diagnostics = await CaptureDiagnosticsAsync(); }
            finally { await DisposeAsync(); }
            throw new InvalidOperationException($"Deployment failed: {exception.Message}\n{diagnostics}", exception);
        }
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

    public Task<PortForwardHandle> StartApiPortForwardAsync() => StartPortForwardAsync("formicae-api", 80);

    private async Task<PortForwardHandle> StartPortForwardAsync(string service, int remotePort)
    {
        var port = GetFreeTcpPort();
        var process = CommandRunner.StartLongRunning(
            "kubectl",
            KubectlArgs(["port-forward",  $"service/{service}", $"{port}:{remotePort}", "-n", Namespace]),
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
        var apiDiagnostics = await RunRolloutDiagnosticsAsync("formicae");
        sections.Add(apiDiagnostics.CombinedOutput);
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
        // Hold the API at zero replicas until the actual pre-loop EF schema and history exist.
        var rendered = await CommandRunner.RunRequiredAsync("kubectl",
            KubectlArgs(["apply", "--dry-run=client", "-k", "deploy/kubernetes/overlays/e2e", "-o", "json"]),
            RepositoryRoot, TimeSpan.FromSeconds(30));
        var manifest = JsonNode.Parse(rendered.StandardOutput)!;
        var api = manifest["items"]!.AsArray().Single(item =>
            item!["kind"]!.GetValue<string>() == "Deployment" && item["metadata"]!["name"]!.GetValue<string>() == "formicae-api")!;
        api["spec"]!["replicas"] = 0;
        api["spec"]!["template"]!["metadata"]!["labels"]!["app.kubernetes.io/instance"] = "formicae";
        var manifestPath = Path.Combine(TempRoot, "upgrade-manifest.json");
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        await KubectlRequiredAsync(["apply", "-f", manifestPath], TimeSpan.FromMinutes(2));
        await KubectlRequiredAsync(["rollout", "status", "deployment/formicae-postgres", "-n", Namespace, "--timeout=180s"], TimeSpan.FromMinutes(4));
        // The deployment has no readiness probe; Running does not mean PostgreSQL accepts connections.
        await KubectlRequiredAsync(["exec", "deployment/formicae-postgres", "-n", Namespace, "--", "sh", "-c",
            "for attempt in $(seq 1 60); do pg_isready -h 127.0.0.1 -U formicae -d formicae && exit 0; sleep 1; done; exit 1"], TimeSpan.FromSeconds(70));
        using (var postgres = await StartPortForwardAsync("formicae-postgres", 5432))
        {
            var connectionString = $"Host=127.0.0.1;Port={postgres.BaseAddress.Port};Database=formicae;Username=formicae;Password=formicae-e2e";
            await using var db = new FormicaeDbContext(new DbContextOptionsBuilder<FormicaeDbContext>().UseNpgsql(connectionString).Options);
            await db.GetService<IMigrator>().MigrateAsync("20260709152649_AddWorkflowTriggerEvents");
            await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-0.7.4.sql")));
        }
        // The fixed image must migrate on startup using the same PostgreSQL PVC.
        await KubectlRequiredAsync(["scale", "deployment/formicae-api", "-n", Namespace, "--replicas=1"], TimeSpan.FromMinutes(1));
        await KubectlRequiredAsync(["rollout", "status", "deployment/formicae-api", "-n", Namespace, "--timeout=180s"], TimeSpan.FromMinutes(4));
    }


    internal Task<CommandResult> RunRolloutDiagnosticsAsync(string release)
        // Windows searches System32 before PATH, where bash.exe is the WSL launcher.
        // Use Git Bash so kubectl and KUBECONFIG remain in the Windows test environment.
        => CommandRunner.RunAsync(OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe")
                : "bash", ["scripts/rollout-diagnostics.sh"], RepositoryRoot,
            TimeSpan.FromMinutes(2), new Dictionary<string, string?>
            {
                ["KUBECONFIG"] = KubeconfigPath,
                ["RELEASE_NAMESPACE"] = Namespace,
                ["RELEASE_NAME"] = release
            });

    public async Task<string> ExerciseFailedRolloutAsync()
    {
        const string name = "diagnostics-api";
        var manifestPath = Path.Combine(TempRoot, "failed-rollout.json");
        var manifest = new
        {
            apiVersion = "apps/v1", kind = "Deployment",
            metadata = new { name, @namespace = Namespace },
            spec = new
            {
                replicas = 1,
                selector = new { matchLabels = new Dictionary<string, string> { ["app"] = name } },
                template = new
                {
                    metadata = new { labels = new Dictionary<string, string>
                    {
                        ["app"] = name, ["app.kubernetes.io/instance"] = "diagnostics", ["app.kubernetes.io/component"] = "api"
                    } },
                    spec = new { volumes = new[] { new { name = "restart-state", emptyDir = new { } } }, containers = new[] { new
                    {
                        name = "crashing-api", image = ApiImage, imagePullPolicy = "IfNotPresent",
                        command = new[] { "/bin/sh", "-c", "if [ -f /state/restarted ]; then echo restarted-but-unready; sleep 300; else touch /state/restarted; echo intentional-rollout-failure; exit 1; fi" },
                        volumeMounts = new[] { new { name = "restart-state", mountPath = "/state" } },
                        readinessProbe = new { exec = new { command = new[] { "/bin/sh", "-c", "exit 1" } } }
                    } } }
                }
            }
        };
        await File.WriteAllTextAsync(manifestPath, System.Text.Json.JsonSerializer.Serialize(manifest));
        try
        {
            await KubectlRequiredAsync(["apply", "-f", manifestPath], TimeSpan.FromSeconds(30));
            var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
            var restarted = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var status = await KubectlAsync(["get", "pods", "-n", Namespace, "-l", $"app={name}",
                    "-o", "jsonpath={.items[0].status.containerStatuses[0].restartCount}"], TimeSpan.FromSeconds(15));
                if (int.TryParse(status.StandardOutput, out var count) && count > 0)
                {
                    restarted = true;
                    break;
                }
                await Task.Delay(1000);
            }
            Assert.True(restarted, "The intentionally failing API container must restart before collecting previous logs.");
            var rollout = await KubectlAsync(["rollout", "status", $"deployment/{name}", "-n", Namespace, "--timeout=5s"], TimeSpan.FromSeconds(15));
            Assert.NotEqual(0, rollout.ExitCode);
            var diagnostics = await RunRolloutDiagnosticsAsync("diagnostics");
            Assert.Equal(0, diagnostics.ExitCode);
            return diagnostics.CombinedOutput;
        }
        finally
        {
            await KubectlAsync(["delete", "deployment", name, "-n", Namespace, "--ignore-not-found"], TimeSpan.FromSeconds(30));
        }
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
