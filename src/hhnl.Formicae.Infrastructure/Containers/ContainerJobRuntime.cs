namespace hhnl.Formicae.Infrastructure.Containers;

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using Microsoft.Extensions.Options;

public enum ContainerEngine
{
    Docker,
    Podman
}

public sealed class ContainerRuntimeOptions
{
    public ContainerEngine Engine { get; set; } = ContainerEngine.Docker;
    public string? Executable { get; set; }
    public string Image { get; set; } = "docker.io/limeray/hhnl-formicae-worker:latest";
    public string? Network { get; set; }
    public string WorkspaceRoot { get; set; } = "formicae-workspaces";
    public int TimeoutSeconds { get; set; } = 1800;
    public bool DeleteFinishedContainers { get; set; } = true;
    public string WorkerCallbackUrl { get; set; } = string.Empty;
    public string WorkerCallbackSecret { get; set; } = string.Empty;
}

public interface IContainerCli
{
    Task<ContainerCliResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public sealed record ContainerCliResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class ProcessContainerCli : IContainerCli
{
    public async Task<ContainerCliResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start '{executable}'.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ContainerCliResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
}

public sealed class ContainerJobRuntime(
    IContainerCli cli,
    IOptions<ContainerRuntimeOptions> options,
    IEnumerable<IWorkflowTickSignal> tickSignals) : IJobRuntime
{
    private const string ManagedByLabel = "formicae.hhnl.de/managed-by";
    private const string ManagedByValue = "formicae";
    private const string JobLabel = "formicae.hhnl.de/job";

    public async Task<RuntimeJobStartResult> StartJobAsync(RuntimeJobSpec spec, CancellationToken cancellationToken)
    {
        var externalId = string.IsNullOrWhiteSpace(spec.Name) ? $"formicae-job-{Guid.NewGuid():N}" : spec.Name;
        var arguments = BuildRunArguments(spec with { Name = externalId });
        var result = await cli.RunAsync(Executable(), arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Container runtime failed to start '{externalId}': {TrimProcessError(result)}");
        }

        StartCompletionSignalWatcher(externalId);
        return new RuntimeJobStartResult(externalId);
    }

    public async Task<RuntimeJobResult?> TryGetJobResultAsync(string externalId, CancellationToken cancellationToken)
    {
        var state = await TryInspectStateAsync(externalId, cancellationToken);
        if (state is null)
        {
            return new RuntimeJobResult(false, externalId, string.Empty, $"Container '{externalId}' was not found.");
        }

        if (state.Running)
        {
            if (IsTimedOut(externalId, state.StartedAt, out var timeoutReason))
            {
                var timeoutLogs = await ReadJobLogsAsync(externalId, CancellationToken.None);
                await RemoveIfConfiguredAsync(externalId, force: true, CancellationToken.None);
                return new RuntimeJobResult(false, externalId, timeoutLogs, timeoutReason);
            }

            return null;
        }

        var logs = await ReadJobLogsAsync(externalId, cancellationToken);
        await RemoveIfConfiguredAsync(externalId, force: false, cancellationToken);
        return state.ExitCode == 0
            ? new RuntimeJobResult(true, externalId, logs, null)
            : new RuntimeJobResult(false, externalId, logs, $"Container '{externalId}' exited with code {state.ExitCode}.");
    }

    public async Task<string> ReadJobLogsAsync(string externalId, CancellationToken cancellationToken)
    {
        var result = await cli.RunAsync(Executable(), ["logs", externalId], cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput : result.StandardError;
    }

    private IReadOnlyList<string> BuildRunArguments(RuntimeJobSpec spec)
    {
        var arguments = new List<string>
        {
            "run",
            "--detach",
            "--name",
            spec.Name,
            "--label",
            $"{ManagedByLabel}={ManagedByValue}",
            "--label",
            $"{JobLabel}={spec.Name}"
        };

        if (!string.IsNullOrWhiteSpace(options.Value.Network))
        {
            arguments.Add("--network");
            arguments.Add(options.Value.Network);
        }

        foreach (var (key, value) in spec.Environment.OrderBy(pair => pair.Key))
        {
            arguments.Add("--env");
            arguments.Add($"{key}={value}");
        }

        if (spec.SecretEnvironment is not null)
        {
            foreach (var (key, value) in spec.SecretEnvironment.Data.OrderBy(pair => pair.Key))
            {
                arguments.Add("--env");
                arguments.Add($"{key}={value}");
            }
        }

        AddContextMount(arguments, spec);
        AddSecretFileMounts(arguments, spec);

        arguments.Add(spec.Image);
        arguments.AddRange(spec.Command);
        return arguments;
    }

    private void AddContextMount(List<string> arguments, RuntimeJobSpec spec)
    {
        if (spec.ContextFiles is not { Count: > 0 })
        {
            return;
        }

        var contextRoot = Path.Combine(WorkspaceRoot(), spec.Name, "context");
        Directory.CreateDirectory(contextRoot);
        foreach (var file in spec.ContextFiles)
        {
            var path = SafeChildPath(contextRoot, file.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content);
        }

        arguments.Add("--volume");
        arguments.Add($"{contextRoot}:{spec.ContextFilesMountPath}:ro");
    }

    private void AddSecretFileMounts(List<string> arguments, RuntimeJobSpec spec)
    {
        if (spec.SecretFiles is not { Count: > 0 })
        {
            return;
        }

        foreach (var secretFile in spec.SecretFiles)
        {
            var secretRoot = Path.Combine(WorkspaceRoot(), spec.Name, "secrets", secretFile.SecretName);
            Directory.CreateDirectory(secretRoot);
            foreach (var (fileName, content) in secretFile.Data)
            {
                var path = SafeChildPath(secretRoot, fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }

            arguments.Add("--volume");
            arguments.Add($"{secretRoot}:{secretFile.MountPath}:ro");
        }
    }

    private bool IsTimedOut(string externalId, DateTimeOffset? startedAt, out string reason)
    {
        if (startedAt is null)
        {
            reason = string.Empty;
            return false;
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.TimeoutSeconds));
        if (DateTimeOffset.UtcNow - startedAt.Value.ToUniversalTime() <= timeout)
        {
            reason = string.Empty;
            return false;
        }

        reason = $"Container '{externalId}' timed out after {options.Value.TimeoutSeconds} seconds.";
        return true;
    }

    private async Task RemoveIfConfiguredAsync(string externalId, bool force, CancellationToken cancellationToken)
    {
        if (!options.Value.DeleteFinishedContainers)
        {
            return;
        }

        IReadOnlyList<string> arguments = force ? ["rm", "--force", externalId] : ["rm", externalId];
        await cli.RunAsync(Executable(), arguments, cancellationToken);
    }

    private void StartCompletionSignalWatcher(string externalId)
    {
        var signal = tickSignals.FirstOrDefault();
        if (signal is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var state = await TryInspectStateAsync(externalId, CancellationToken.None);
                    if (state is null)
                    {
                        return;
                    }

                    if (!state.Running || IsTimedOut(externalId, state.StartedAt, out _))
                    {
                        signal.Signal();
                        return;
                    }
                }
                catch
                {
                    // The periodic orchestration loop remains the durable fallback.
                }

                await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
        });
    }

    private async Task<ContainerState?> TryInspectStateAsync(string externalId, CancellationToken cancellationToken)
    {
        var inspect = await cli.RunAsync(Executable(), ["inspect", externalId], cancellationToken);
        return inspect.ExitCode == 0 ? ParseState(inspect.StandardOutput) : null;
    }

    private string Executable()
        => !string.IsNullOrWhiteSpace(options.Value.Executable)
            ? options.Value.Executable
            : options.Value.Engine == ContainerEngine.Podman ? "podman" : "docker";

    private string WorkspaceRoot()
        => Path.GetFullPath(string.IsNullOrWhiteSpace(options.Value.WorkspaceRoot) ? "formicae-workspaces" : options.Value.WorkspaceRoot);

    private static string SafeChildPath(string root, string child)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, child));
        var relativePath = Path.GetRelativePath(fullRoot, fullPath);
        if (relativePath == ".." || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Runtime file path '{child}' escapes the workspace root.");
        }

        return fullPath;
    }

    private static ContainerState ParseState(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().First().GetProperty("State")
            : document.RootElement.GetProperty("State");
        var running = root.TryGetProperty("Running", out var runningElement) && runningElement.GetBoolean();
        var exitCode = root.TryGetProperty("ExitCode", out var exitCodeElement) ? exitCodeElement.GetInt32() : -1;
        DateTimeOffset? startedAt = null;
        if (root.TryGetProperty("StartedAt", out var startedAtElement)
            && DateTimeOffset.TryParse(startedAtElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            startedAt = parsed;
        }

        return new ContainerState(running, exitCode, startedAt);
    }

    private static string TrimProcessError(ContainerCliResult result)
        => string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim();

    private sealed record ContainerState(bool Running, int ExitCode, DateTimeOffset? StartedAt);
}
