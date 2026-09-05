using System.Diagnostics;

namespace hhnl.Formicae.Tests;

public sealed class RolloutDiagnosticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Diagnostics_CollectEveryContainer_WithoutMaskingFailures(bool discoveryFails)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "hhnl.Formicae.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var workflow = await File.ReadAllTextAsync(Path.Combine(root.FullName, ".github/workflows/deploy-formicae.yml"));
        Assert.Matches(@"(?s)Wait for API rollout.*if: failure\(\).*run: bash ./scripts/rollout-diagnostics.sh", workflow);

        var bash = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe")
            : "bash";
        var start = new ProcessStartInfo(bash)
        {
            WorkingDirectory = root.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("""
            kubectl() {
              printf '%s\n' "$*" >&2
              if [[ "$DISCOVERY_FAILS" == true ]]; then return 1; fi
              case "$*" in
                'get pods '*'-o name') echo 'pod/api-one pod/api-two' ;;
                'get pod/'*) echo 'init api sidecar' ;;
                *'--previous'*|'describe '*) return 1 ;;
              esac
            }
            source scripts/rollout-diagnostics.sh
            """);
        start.Environment["RELEASE_NAMESPACE"] = "test-namespace";
        start.Environment["RELEASE_NAME"] = "test-release";
        start.Environment["DISCOVERY_FAILS"] = discoveryFails ? "true" : "false";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await stdout;
            var calls = await stderr;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("get deployments,replicasets,pods -n test-namespace -o wide", calls);
            Assert.Contains("describe deployment test-release-api", calls);
            Assert.Contains("--sort-by=.metadata.creationTimestamp", calls);
            Assert.Contains("app.kubernetes.io/instance=test-release,app.kubernetes.io/component=api", calls);
            if (discoveryFails)
            {
                Assert.DoesNotContain("logs pod/", calls);
                return;
            }
            foreach (var pod in new[] { "api-one", "api-two" })
            foreach (var container in new[] { "init", "api", "sidecar" })
            {
                Assert.Contains($"logs pod/{pod} -n test-namespace -c {container} --tail=200", calls);
                Assert.Contains($"logs pod/{pod} -n test-namespace -c {container} --previous --tail=200", calls);
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
