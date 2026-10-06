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
    public Dictionary<string, Dictionary<string, string>> StepSecrets { get; set; } = new(StringComparer.Ordinal);
}

public interface IContainerCli
{
    Task<ContainerCliResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
    Task<ContainerCliResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
        => environment.Count == 0 ? RunAsync(executable, arguments, cancellationToken)
            : Task.FromException<ContainerCliResult>(new NotSupportedException("This container adapter does not support private child-process secret injection."));
}

public sealed record ContainerCliResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class ProcessContainerCli : IContainerCli
{
    public async Task<ContainerCliResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => await RunAsync(executable, arguments, new Dictionary<string, string>(), cancellationToken);

    public async Task<ContainerCliResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
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
        foreach (var (name, value) in environment) startInfo.Environment[name] = value;

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
    private const string ManagedByLabel = "formicae.managed-by";
    private const string ManagedByValue = "formicae";
    private const string JobLabel = "formicae.job";
    private const string TimeoutLabel = "formicae.timeout-seconds";

    public async Task<RuntimeJobStartResult> StartJobAsync(RuntimeJobSpec spec, CancellationToken cancellationToken)
    {
        RuntimeJobExtensions.Validate(spec);
        if (spec.ExecutionRequirements?.RequiresNestedContainers == true)
            throw new InvalidOperationException("Nested-container capability requires the Kubernetes runtime. Remove that capability for Docker/Podman execution.");
        if (spec.ImagePullSecretNames is { Count: > 0 })
            throw new InvalidOperationException("Named Kubernetes image pull Secrets cannot be used by the container runtime. Configure registry authentication on Docker/Podman instead.");
        var privateEnvironment = ResolveSecretEnvironment(spec);
        var externalId = string.IsNullOrWhiteSpace(spec.Name) ? $"formicae-job-{Guid.NewGuid():N}" : spec.Name;
        if (spec.ReuseExisting && await TryAttachExistingAsync(externalId, cancellationToken))
        {
            StartCompletionSignalWatcher(externalId);
            return new RuntimeJobStartResult(externalId);
        }
        var arguments = BuildRunArguments(spec with { Name = externalId });
        var result = await cli.RunAsync(Executable(), arguments, privateEnvironment, cancellationToken);
        if (result.ExitCode != 0)
        {
            // Another scheduler may have launched the same durable attempt after our inspection.
            if (spec.ReuseExisting && await TryAttachExistingAsync(externalId, cancellationToken))
            {
                StartCompletionSignalWatcher(externalId);
                return new RuntimeJobStartResult(externalId);
            }
            throw new InvalidOperationException($"Container runtime failed to start '{externalId}': {RuntimeJobExtensions.Redact(TrimProcessError(result), privateEnvironment.Values)}");
        }

        StartCompletionSignalWatcher(externalId);
        return new RuntimeJobStartResult(externalId);
    }

    private async Task<bool> TryAttachExistingAsync(string externalId, CancellationToken cancellationToken)
    {
        var inspect = await cli.RunAsync(Executable(), ["inspect", externalId], cancellationToken);
        if (inspect.ExitCode != 0) return false;
        using var document = JsonDocument.Parse(inspect.StandardOutput);
        var container = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().First() : document.RootElement;
        if (!container.TryGetProperty("Config", out var config)
            || !config.TryGetProperty("Labels", out var labels)
            || !labels.TryGetProperty(ManagedByLabel, out var managedBy) || managedBy.GetString() != ManagedByValue
            || !labels.TryGetProperty(JobLabel, out var job) || job.GetString() != externalId)
            throw new InvalidOperationException($"Container '{externalId}' already exists but is not owned by this Formicae job.");
        if (container.TryGetProperty("State", out var state)
            && state.TryGetProperty("Status", out var status) && status.GetString() == "created")
        {
            var started = await cli.RunAsync(Executable(), ["start", externalId], cancellationToken);
            if (started.ExitCode != 0)
                throw new InvalidOperationException($"Container runtime failed to start existing '{externalId}': {TrimProcessError(started)}");
        }
        return true;
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
            if (IsTimedOut(externalId, state, out var timeoutReason))
            {
                var timeoutLogs = await ReadJobLogsAsync(externalId, CancellationToken.None);
                await CancelJobAsync(externalId, CancellationToken.None);
                return new RuntimeJobResult(false, externalId, timeoutLogs, timeoutReason);
            }

            return null;
        }

        var logs = await ReadJobLogsAsync(externalId, cancellationToken);
        return state.ExitCode == 0
            ? new RuntimeJobResult(true, externalId, logs, null, state.ExitCode)
            : new RuntimeJobResult(false, externalId, logs, $"Container '{externalId}' exited with code {state.ExitCode}.", state.ExitCode);
    }

    public async Task<string> ReadJobLogsAsync(string externalId, CancellationToken cancellationToken)
    {
        var result = await cli.RunAsync(Executable(), ["logs", externalId], cancellationToken);
        return result.ExitCode == 0 ? string.Join("\n", new[] { result.StandardOutput, result.StandardError }.Where(value => !string.IsNullOrWhiteSpace(value))) : result.StandardError.Contains("No such", StringComparison.OrdinalIgnoreCase) ? "[Runtime logs unavailable: worker no longer exists.]" : throw new InvalidOperationException($"Container log read failed: {result.StandardError}");
    }

    public async Task CancelJobAsync(string externalId, CancellationToken cancellationToken)
    {
        if (await TryInspectStateAsync(externalId, cancellationToken) is not { Running: true }) return;
        var result = await cli.RunAsync(Executable(), ["stop", "--time", "10", externalId], cancellationToken);
        if (result.ExitCode != 0 && !result.StandardError.Contains("No such", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Container cancellation failed: {result.StandardError}");
    }

    public Task AcknowledgeCompletionAsync(string externalId, CancellationToken cancellationToken)
        => RemoveIfConfiguredAsync(externalId, force: false, cancellationToken);

    private IReadOnlyList<string> BuildRunArguments(RuntimeJobSpec spec)
    {
        var executionPolicy = ResolveExecutionPolicy(spec);
        var arguments = new List<string>
        {
            "run",
            "--detach",
            "--name",
            spec.Name,
            "--label",
            $"{ManagedByLabel}={ManagedByValue}",
            "--label",
            $"{JobLabel}={spec.Name}",
            "--label",
            $"{TimeoutLabel}={executionPolicy.TimeoutSeconds + executionPolicy.StartupGraceSeconds}"
        };
        arguments.AddRange(["--pull", spec.ImagePullPolicy switch { "Always" => "always", "Never" => "never", _ => "missing" }]);

        if (!string.IsNullOrWhiteSpace(options.Value.Network))
        {
            arguments.Add("--network");
            arguments.Add(options.Value.Network);
        }

        var environment = spec.Environment.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (spec.ExecutionRequirements?.RequiresBrowser == true) environment["FORMICAE_REQUIRES_BROWSER"] = "true";
        if (spec.ExecutionPolicy is not null || spec.TimeoutLimitSeconds is not null)
        {
            environment["FORMICAE_JOB_TIMEOUT_SECONDS"] = Math.Max(1, executionPolicy.TimeoutSeconds).ToString(CultureInfo.InvariantCulture);
            environment["FORMICAE_CHECKPOINT_GRACE_SECONDS"] = Math.Clamp(executionPolicy.CheckpointGraceSeconds, 0, Math.Max(0, executionPolicy.TimeoutSeconds - 1)).ToString(CultureInfo.InvariantCulture);
        }
        if (spec.TimeoutLimitSeconds is not null) environment["FORMICAE_ENVIRONMENT_TIMEOUT_LIMIT"] = "true";

        foreach (var (key, value) in environment.OrderBy(pair => pair.Key))
        {
            arguments.Add("--env");
            arguments.Add(IsPrivateRuntimeVariable(key) ? key : $"{key}={value}");
        }

        if (spec.SecretEnvironment is not null)
        {
            foreach (var key in spec.SecretEnvironment.Data.Keys.OrderBy(key => key))
            {
                arguments.Add("--env");
                arguments.Add(key);
            }
        }
        foreach (var reference in spec.SecretReferences ?? []) arguments.AddRange(["--env", reference.EnvironmentName]);

        AddContextMount(arguments, spec);
        AddSecretFileMounts(arguments, spec);

        arguments.Add(spec.Image);
        arguments.AddRange(spec.Command);
        return arguments;
    }

    private static bool IsPrivateRuntimeVariable(string name)
        => name is "FORMICAE_GIT_ACCESS_TOKEN" or "FORMICAE_WORKER_CALLBACK_SECRET" or "LLM_API_KEY" or "OPENAI_API_KEY" or "ANTHROPIC_API_KEY";

    private IReadOnlyDictionary<string, string> ResolveSecretEnvironment(RuntimeJobSpec spec)
    {
        var values = spec.SecretEnvironment?.Data.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in spec.Environment.Where(pair => IsPrivateRuntimeVariable(pair.Key))) values[name] = value;
        foreach (var reference in spec.SecretReferences ?? [])
        {
            if (!options.Value.StepSecrets.TryGetValue(reference.SecretName, out var configured)
                || !configured.TryGetValue(reference.Key, out var value))
                throw new InvalidOperationException($"Required local Secret '{reference.SecretName}' key '{reference.Key}' is not configured.");
            values.Add(reference.EnvironmentName, value);
        }
        return values;
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
            WriteJobFile(path, file.Content, spec.ReuseExisting);
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
                WriteJobFile(path, content, spec.ReuseExisting);
            }

            arguments.Add("--volume");
            arguments.Add($"{secretRoot}:{secretFile.MountPath}:ro");
        }
    }

    private bool IsTimedOut(string externalId, ContainerState state, out string reason)
    {
        if (state.StartedAt is null)
        {
            reason = string.Empty;
            return false;
        }

        var timeoutSeconds = Math.Max(1, state.TimeoutSeconds ?? options.Value.TimeoutSeconds);
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        if (DateTimeOffset.UtcNow - state.StartedAt.Value.ToUniversalTime() <= timeout)
        {
            reason = string.Empty;
            return false;
        }

        reason = $"Container '{externalId}' timed out after {timeoutSeconds} seconds.";
        return true;
    }

    private RuntimeJobExecutionPolicy ResolveExecutionPolicy(RuntimeJobSpec spec)
        => RuntimeJobPolicyResolver.Resolve(spec, options.Value.TimeoutSeconds);

    private async Task RemoveIfConfiguredAsync(string externalId, bool force, CancellationToken cancellationToken)
    {
        if (!options.Value.DeleteFinishedContainers)
        {
            return;
        }

        IReadOnlyList<string> arguments = force ? ["rm", "--force", externalId] : ["rm", externalId];
        var result = await cli.RunAsync(Executable(), arguments, cancellationToken);
        if (result.ExitCode != 0 && !result.StandardError.Contains("No such", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Container cleanup failed: {result.StandardError}");
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

                    if (!state.Running || IsTimedOut(externalId, state, out _))
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
        if (inspect.ExitCode == 0) return ParseState(inspect.StandardOutput);
        if (inspect.StandardError.Contains("No such", StringComparison.OrdinalIgnoreCase)
            || inspect.StandardError.Contains("not found", StringComparison.OrdinalIgnoreCase)) return null;
        throw new InvalidOperationException($"Container status read failed: {inspect.StandardError}");
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

    private static void WriteJobFile(string path, string content, bool reuseExisting)
    {
        if (!reuseExisting)
        {
            File.WriteAllText(path, content);
            return;
        }
        // A durable attempt owns immutable inputs, even if another launcher wins after inspection.
        FileStream stream;
        try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        catch (IOException) when (File.Exists(path)) { return; }
        using (stream)
        using (var writer = new StreamWriter(stream)) writer.Write(content);
    }

    private static ContainerState ParseState(string json)
    {
        using var document = JsonDocument.Parse(json);
        var container = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().First()
            : document.RootElement;
        var state = container.GetProperty("State");
        var running = state.TryGetProperty("Running", out var runningElement) && runningElement.GetBoolean();
        var exitCode = state.TryGetProperty("ExitCode", out var exitCodeElement) ? exitCodeElement.GetInt32() : -1;
        DateTimeOffset? startedAt = null;
        if (state.TryGetProperty("StartedAt", out var startedAtElement)
            && DateTimeOffset.TryParse(startedAtElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            startedAt = parsed;
        }

        int? timeoutSeconds = null;
        if (container.TryGetProperty("Config", out var config)
            && config.TryGetProperty("Labels", out var labels)
            && labels.ValueKind == JsonValueKind.Object
            && labels.TryGetProperty(TimeoutLabel, out var timeoutLabel)
            && int.TryParse(timeoutLabel.GetString(), CultureInfo.InvariantCulture, out var parsedTimeout))
        {
            timeoutSeconds = parsedTimeout;
        }

        return new ContainerState(running, exitCode, startedAt, timeoutSeconds);
    }

    private static string TrimProcessError(ContainerCliResult result)
        => string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim();

    private sealed record ContainerState(bool Running, int ExitCode, DateTimeOffset? StartedAt, int? TimeoutSeconds);
}
