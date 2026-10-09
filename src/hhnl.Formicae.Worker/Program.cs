using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using hhnl.Formicae.Application.Workflows;

if (args is ["--check-runtime"]) {
    if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return 1;
    foreach (var command in new[] { "git", "sh", "node", "npx", "python3", "openhands" }) {
        using var check = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "command -v " + command }, RedirectStandardOutput = true, RedirectStandardError = true });
        if (check is null) return 1;
        await check.WaitForExitAsync();
        if (check.ExitCode != 0) return 1;
    }
    Console.WriteLine("Formicae worker runtime protocol 1: linux/amd64 compatible.");
    return 0;
}
var environment = WorkerEnvironment.Load();
using var reporter = new WorkerReporter(environment.CallbackUrl, environment.CallbackSecret, environment.WorkflowId, environment.TaskKind, environment.ExternalId, environment.ExecutionAttemptId);
using var shutdown = new CancellationTokenSource();
using var sigterm = OperatingSystem.IsLinux()
    ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        shutdown.Cancel();
    })
    : null;
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    await reporter.ReportAsync("worker", "Formicae worker started.");
    var exitCode = await WorkerCommand.RunAsync(environment, reporter, shutdown.Token);
    if (environment.TaskKind == "Script") await reporter.ReportScriptResultAsync(exitCode);
    await reporter.ReportAsync("worker", $"Formicae worker finished with exit code {exitCode}.");
    return exitCode;
}
catch (Exception exception)
{
    await reporter.ReportAsync("worker-error", exception.ToString());
    if (environment.TaskKind == "Script") await reporter.ReportScriptResultAsync(1);
    return 1;
}

finally
{
    await reporter.FlushAsync();
}

internal sealed record WorkerEnvironment(
    Guid WorkflowId,
    string TaskKind,
    string RepositoryUrl,
    string Branch,
    string Prompt,
    string? Model,
    string AuthMethod,
    string ExternalId,
    Uri? CallbackUrl,
    string? CallbackSecret,
    string? AiSettingsId,
    string? CodexLoginCommand,
    string ContextPath,
    string? GitAccessToken,
    bool RequiresBrowser,
    bool RequiresNestedContainers,
    int? JobTimeoutSeconds,
    int CheckpointGraceSeconds,
    bool EnvironmentTimeoutLimit = false,
    Guid? ExecutionAttemptId = null,
    EnvironmentConfiguration? ExecutionConfiguration = null,
    WorkflowScriptSettings? Script = null,
    IReadOnlyList<CustomTaskOutputDefinition>? OutputSchema = null)
{
    public static WorkerEnvironment Load()
    {
        var workflowId = Guid.Parse(Required("FORMICAE_WORKFLOW_ID"));
        return new WorkerEnvironment(
            workflowId,
            Required("FORMICAE_TASK_KIND"),
            Required("FORMICAE_REPOSITORY_URL"),
            Required("FORMICAE_BRANCH"),
            Required("FORMICAE_TASK_PROMPT"),
            Optional("FORMICAE_MODEL"),
            Optional("FORMICAE_OPENHANDS_AUTH_METHOD") ?? "ApiKey",
            Optional("FORMICAE_EXTERNAL_ID") ?? Environment.MachineName,
            Uri.TryCreate(Optional("FORMICAE_WORKER_CALLBACK_URL"), UriKind.Absolute, out var callbackUrl) ? callbackUrl : null,
            Optional("FORMICAE_WORKER_CALLBACK_SECRET"),
            Optional("FORMICAE_AI_SETTINGS_ID"),
            Optional("FORMICAE_CODEX_LOGIN_COMMAND"),
            Optional("FORMICAE_CONTEXT_PATH") ?? "/workspace/formicae/context",
            Optional("FORMICAE_GIT_ACCESS_TOKEN"),
            IsTrue("FORMICAE_REQUIRES_BROWSER"),
            IsTrue("FORMICAE_REQUIRES_NESTED_CONTAINERS"),
            OptionalPositiveInt("FORMICAE_JOB_TIMEOUT_SECONDS"),
            OptionalNonNegativeInt("FORMICAE_CHECKPOINT_GRACE_SECONDS"),
            IsTrue("FORMICAE_ENVIRONMENT_TIMEOUT_LIMIT"),
            Guid.TryParse(Optional("FORMICAE_EXECUTION_ATTEMPT_ID"), out var attempt) ? attempt : null,
            JsonSerializer.Deserialize<EnvironmentConfiguration>(Optional("FORMICAE_EXECUTION_CONFIGURATION") ?? "{}", JsonSerializerOptions.Web),
            Optional("FORMICAE_SCRIPT_SETTINGS") is { } script ? JsonSerializer.Deserialize<WorkflowScriptSettings>(script, JsonSerializerOptions.Web) : null,
            Optional("FORMICAE_OUTPUT_SCHEMA") is { } outputs ? JsonSerializer.Deserialize<CustomTaskOutputDefinition[]>(outputs, JsonSerializerOptions.Web) : null);
    }

    public bool UsesCodexSubscription => string.Equals(AuthMethod, "CodexSubscription", StringComparison.OrdinalIgnoreCase);
    public bool IsCodexAuthSetup => TaskKind is "CodexAuthSetup" || string.Equals(AuthMethod, "CodexSubscriptionSetup", StringComparison.OrdinalIgnoreCase);
    public bool RequiresRepositoryCheckout => TaskKind is "Plan" or "Implement" or "AddressComments" || TaskKind == "Script" && Script?.WorkingDirectory == "repository";
    public bool CanCommitRepositoryChanges => TaskKind is "Implement" or "AddressComments";
    public bool RequiresHardEnvironmentDeadline => EnvironmentTimeoutLimit && TaskKind != "Custom"
        && (!UsesCodexSubscription || !CanCommitRepositoryChanges || CheckpointGraceSeconds <= 0);

    private static string Required(string name)
        => Optional(name) ?? throw new InvalidOperationException($"Required environment variable '{name}' is missing.");

    private static string? Optional(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool IsTrue(string name)
        => bool.TryParse(Optional(name), out var value) && value;

    private static int? OptionalPositiveInt(string name)
        => int.TryParse(Optional(name), out var value) && value > 0 ? value : null;

    private static int OptionalNonNegativeInt(string name)
        => int.TryParse(Optional(name), out var value) && value >= 0 ? value : 0;
}

internal static partial class WorkerCommand
{
    internal const int CheckpointExitCode = 75;
    private const string WorkspaceDirectory = "/workspace";
    private const string RepositoryDirectory = "/workspace/repo";
    private const string NonInteractivePlanningInstructions =
        "This is a non-interactive Formicae planning run. Do not ask the user questions, request a mode change, " +
        "ask for the request to be resent, or invoke interactive shaping skills. Inspect the repository and " +
        "available product context directly, make conservative assumptions where needed, and return a complete, actionable plan.";

    public static async Task<int> RunAsync(
        WorkerEnvironment environment,
        WorkerReporter reporter,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null,
        string? workspaceDirectory = null)
    {
        workspaceDirectory ??= WorkspaceDirectory;
        Directory.CreateDirectory(workspaceDirectory);
        if (environment.TaskKind == "ModelDiscovery")
        {
            var discoveryExit = await CodexModelDiscovery.RunAsync(cancellationToken);
            await reporter.ReportCodexAuthAsync(environment.AiSettingsId, ReadCodexAuth(), cancellationToken);
            return discoveryExit;
        }
        if (environment.IsCodexAuthSetup)
        {
            return await RunCodexAuthSetupAsync(environment, reporter, cancellationToken);
        }

        if (environment.RequiresHardEnvironmentDeadline || environment.TaskKind is "Script" or "Custom")
            return await RunWithHardDeadlineAsync(environment.JobTimeoutSeconds, reporter, timeProvider ?? TimeProvider.System,
                cancellationToken, token => RunTaskAsync(environment, reporter, token, timeProvider, workspaceDirectory));
        return await RunTaskAsync(environment, reporter, cancellationToken, timeProvider, workspaceDirectory);
    }

    private static async Task<int> RunTaskAsync(WorkerEnvironment environment, WorkerReporter reporter,
        CancellationToken cancellationToken, TimeProvider? timeProvider, string workspaceDirectory)
    {

        var bootstrapExit = await WorkerExtensions.InstallToolsAsync(environment.ExecutionConfiguration?.Tools ?? [], reporter,
            timeProvider ?? TimeProvider.System, cancellationToken, workspaceDirectory);
        if (bootstrapExit != 0) return bootstrapExit;
        if (environment.TaskKind != "Script" && !environment.UsesCodexSubscription) WorkerExtensions.ConfigureOpenHands(environment.ExecutionConfiguration?.McpServers ?? [], environment.RequiresBrowser);

        if (environment.RequiresNestedContainers && !await WaitForDockerAsync(reporter, cancellationToken))
        {
            return 1;
        }

        var workingDirectory = workspaceDirectory;
        if (environment.RequiresRepositoryCheckout)
        {
            workingDirectory = RepositoryDirectory;
            var checkoutExit = await CheckoutRepositoryAsync(environment, reporter, cancellationToken);
            if (checkoutExit != 0)
            {
                return checkoutExit;
            }
        }

        if (environment.TaskKind == "Custom")
        {
            return await RunCustomCommandAsync(environment, workingDirectory, reporter, timeProvider ?? TimeProvider.System, cancellationToken);
        }

        if (environment.TaskKind == "Script")
            return await WorkerExtensions.RunScriptAsync(environment.Script ?? throw new InvalidOperationException("Script configuration is missing."),
                workingDirectory, reporter, cancellationToken);

        if (environment.UsesCodexSubscription)
        {
            return await RunCodexAsync(environment, workingDirectory, reporter, timeProvider ?? TimeProvider.System, cancellationToken);
        }

        return await RunProcessAsync(
            "openhands",
            ["--headless", "--json", "--override-with-envs", "-t", environment.Prompt],
            environment.RequiresRepositoryCheckout ? workingDirectory : null,
            reporter,
            cancellationToken);
    }

    internal static async Task<int> RunWithHardDeadlineAsync(int? timeoutSeconds, WorkerReporter reporter,
        TimeProvider timeProvider, CancellationToken cancellationToken, Func<CancellationToken, Task<int>> execute)
    {
        if (timeoutSeconds is not (>= 1 and <= 3600))
            throw new InvalidOperationException("Environment-capped tasks require a timeout between 1 and 3600 seconds.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds.Value), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try { return await execute(linked.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await reporter.ReportAsync("worker-error", "Environment execution deadline exceeded.", cancellationToken);
            return 124;
        }
    }

    internal static async Task<int> RunCustomCommandAsync(WorkerEnvironment environment, string workingDirectory,
        WorkerReporter reporter, TimeProvider timeProvider, CancellationToken cancellationToken,
        Func<string, IReadOnlyList<string>, string, CancellationToken, Task<int>>? execute = null,
        Func<string, IReadOnlyList<string>, string, CancellationToken, Action<string>, Task<int>>? executeWithOutput = null)
    {
        if (environment.JobTimeoutSeconds is not (>= 1 and <= 3600))
            throw new InvalidOperationException("Custom tasks require a timeout between 1 and 3600 seconds.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(environment.JobTimeoutSeconds.Value), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        if (execute is null)
        {
            if (environment.UsesCodexSubscription) CodexWorkspace.Prepare(environment.RequiresBrowser, environment.ExecutionConfiguration?.McpServers ?? []);
            execute = (file, arguments, directory, token) => RunProcessAsync(file, arguments, directory, reporter, token);
        }
        try
        {
            if (environment.OutputSchema is { Count: > 0 })
                return await RunOutputTaskAsync(environment, workingDirectory, reporter, linked.Token, executeWithOutput);
            var exit = await execute(environment.UsesCodexSubscription ? "npx" : "openhands",
                environment.UsesCodexSubscription ? BuildCodexArguments(environment, workingDirectory)
                    : ["--headless", "--json", "--override-with-envs", "-t", environment.Prompt], workingDirectory, linked.Token);
            if (environment.UsesCodexSubscription)
                await reporter.ReportCodexAuthAsync(environment.AiSettingsId, ReadCodexAuth(), linked.Token);
            return exit;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await reporter.ReportAsync("worker-error", "Custom task execution deadline exceeded.", cancellationToken);
            return 124;
        }
    }

    private static async Task<int> RunCodexAuthSetupAsync(WorkerEnvironment environment, WorkerReporter reporter, CancellationToken cancellationToken)
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") ?? "/tmp/codex-home";
        Directory.CreateDirectory(codexHome);
        var command = string.IsNullOrWhiteSpace(environment.CodexLoginCommand)
            ? "npx -y @openai/codex login --device-auth"
            : environment.CodexLoginCommand;

        await reporter.ReportAsync("worker", "Starting Codex subscription login.", cancellationToken);
        var exitCode = await RunProcessAsync("/bin/sh", ["-lc", command], WorkspaceDirectory, reporter, cancellationToken);
        var codexAuth = ReadCodexAuth();
        await reporter.ReportCodexAuthAsync(environment.AiSettingsId, codexAuth, cancellationToken);
        if (exitCode == 0 && string.IsNullOrWhiteSpace(codexAuth))
        {
            await reporter.ReportAsync("worker-error", "Codex login completed without producing auth.json.", cancellationToken);
            return 1;
        }

        return exitCode;
    }
    private static async Task<int> RunCodexAsync(
        WorkerEnvironment environment,
        string workingDirectory,
        WorkerReporter reporter,
        TimeProvider timeProvider,
        CancellationToken shutdownToken)
    {
        CodexWorkspace.Prepare(environment.RequiresBrowser, environment.ExecutionConfiguration?.McpServers ?? []);

        var args = BuildCodexArguments(environment, workingDirectory);
        var deadline = WorkerDeadlinePolicy.From(environment);
        if (deadline is null)
        {
            var codexExit = await RunProcessAsync("npx", args, workingDirectory, reporter, shutdownToken, environment.GitAccessToken);
            await reporter.ReportCodexAuthAsync(environment.AiSettingsId, ReadCodexAuth(), shutdownToken);
            return codexExit != 0 || !environment.CanCommitRepositoryChanges
                ? codexExit
                : (await CommitAndPushAsync(environment, workingDirectory, reporter, checkpoint: false, shutdownToken)).ExitCode;
        }

        string? threadId = null;
        var processStarted = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codexTask = RunProcessAsync(
            "npx",
            args,
            workingDirectory,
            reporter,
            CancellationToken.None,
            environment.GitAccessToken,
            processStarted,
            line => threadId ??= TryReadCodexThreadId(line));
        var softDeadline = Task.Delay(deadline.SoftTimeout, timeProvider, CancellationToken.None);
        var shutdownRequested = WaitForCancellationAsync(shutdownToken);
        var completed = await Task.WhenAny(codexTask, softDeadline, shutdownRequested);
        if (codexTask.IsCompleted && !shutdownToken.IsCancellationRequested)
        {
            var codexExit = await codexTask;
            await reporter.ReportCodexAuthAsync(environment.AiSettingsId, ReadCodexAuth(), shutdownToken);
            return codexExit != 0
                ? codexExit
                : (await CommitAndPushAsync(environment, workingDirectory, reporter, checkpoint: false, shutdownToken)).ExitCode;
        }

        var externalShutdown = completed == shutdownRequested;
        var checkpointReason = externalShutdown
            ? "The worker received a shutdown signal."
            : $"The execution deadline is approaching; {environment.CheckpointGraceSeconds} seconds remain.";
        await reporter.ReportAsync("worker-checkpoint", checkpointReason, CancellationToken.None);

        if (processStarted.Task.IsCompletedSuccessfully)
        {
            await InterruptProcessAsync(processStarted.Task.Result);
        }
        await IgnoreProcessFailureAsync(codexTask);

        if (!externalShutdown && !string.IsNullOrWhiteSpace(threadId))
        {
            var resumeArgs = BuildCodexResumeArguments(environment, workingDirectory, threadId);
            await RunProcessForDurationAsync(
                "npx",
                resumeArgs,
                workingDirectory,
                reporter,
                deadline.FinalizationTimeout,
                timeProvider,
                environment.GitAccessToken);
        }

        await reporter.ReportCodexAuthAsync(environment.AiSettingsId, ReadCodexAuth(), CancellationToken.None);
        using var checkpointTimeout = new CancellationTokenSource(deadline.CheckpointTimeout);
        var checkpoint = await CommitAndPushAsync(environment, workingDirectory, reporter, checkpoint: true, checkpointTimeout.Token);
        var marker = JsonSerializer.Serialize(new WorkerCheckpointResult(
            "formicae.checkpoint",
            environment.Branch,
            checkpoint.CommitSha,
            checkpoint.Changed,
            checkpoint.ExitCode == 0,
            checkpointReason), JsonSerializerOptions.Web);
        await reporter.ReportAsync("worker-checkpoint", marker, CancellationToken.None);
        return CheckpointExitCode;
    }

    private static async Task<CommitResult> CommitAndPushAsync(
        WorkerEnvironment environment,
        string workingDirectory,
        WorkerReporter reporter,
        bool checkpoint,
        CancellationToken cancellationToken)
    {
        foreach (var command in new[]
        {
            new[] { "config", "user.email", "formicae@example.invalid" },
            new[] { "config", "user.name", "Formicae Agent" }
        })
        {
            var exit = await RunProcessAsync("git", command, workingDirectory, reporter, cancellationToken, environment.GitAccessToken);
            if (exit != 0)
            {
                return new CommitResult(exit, null, Changed: false);
            }
        }

        var statusOutput = await CaptureProcessAsync("git", ["status", "--porcelain"], workingDirectory, cancellationToken);
        if (string.IsNullOrWhiteSpace(statusOutput.Output))
        {
            await reporter.ReportAsync("worker", checkpoint
                ? "Checkpoint requested, but the worktree has no uncommitted changes."
                : "Codex completed without uncommitted file changes.", cancellationToken);
            return new CommitResult(0, null, Changed: false);
        }

        var addExit = await RunProcessAsync("git", ["add", "-A"], workingDirectory, reporter, cancellationToken, environment.GitAccessToken);
        if (addExit != 0)
        {
            return new CommitResult(addExit, null, Changed: true);
        }

        var subject = checkpoint
            ? $"Checkpoint Formicae workflow {environment.WorkflowId:N} before deadline"
            : environment.TaskKind == "AddressComments"
            ? $"Address comments for Formicae workflow {environment.WorkflowId:N}"
            : $"Implement Formicae workflow {environment.WorkflowId:N}";
        var commitExit = await RunProcessAsync("git", ["commit", "-m", subject], workingDirectory, reporter, cancellationToken, environment.GitAccessToken);
        if (commitExit != 0)
        {
            return new CommitResult(commitExit, null, Changed: true);
        }

        var revision = await CaptureProcessAsync("git", ["rev-parse", "HEAD"], workingDirectory, cancellationToken);
        var commitSha = revision.ExitCode == 0 ? revision.Output.Trim() : null;

        if (!string.IsNullOrWhiteSpace(environment.GitAccessToken))
        {
            var remoteExit = await RunProcessAsync(
                "git",
                ["remote", "set-url", "origin", BuildAuthenticatedRepositoryUrl(environment.RepositoryUrl, environment.GitAccessToken)],
                workingDirectory,
                reporter,
                cancellationToken,
                environment.GitAccessToken);
            if (remoteExit != 0)
            {
                return new CommitResult(remoteExit, commitSha, Changed: true);
            }
        }

        var pushExit = await RunProcessAsync("git", ["push", "origin", environment.Branch], workingDirectory, reporter, cancellationToken, environment.GitAccessToken);
        return new CommitResult(pushExit, commitSha, Changed: true);
    }

    internal static List<string> BuildCodexArguments(WorkerEnvironment environment, string workingDirectory)
    {
        var args = new List<string> { "-y", "@openai/codex", "exec" };
        if (environment.TaskKind == "Plan")
        {
            args.AddRange(["-c", $"developer_instructions={NonInteractivePlanningInstructions}"]);
        }
        else if (WorkerDeadlinePolicy.From(environment) is { } deadline)
        {
            args.AddRange(["-c", $"developer_instructions={BuildDeadlineInstructions(deadline)}"]);
        }

        if (!string.IsNullOrWhiteSpace(environment.Model))
        {
            args.Add("-m");
            args.Add(environment.Model);
        }

        args.AddRange(["-c", $"projects={{{WorkerExtensions.QuoteToml(workingDirectory)}={{trust_level=\"untrusted\"}}}}"]);
        args.AddRange(["-C", workingDirectory, "--skip-git-repo-check", "--json", "--dangerously-bypass-approvals-and-sandbox", environment.Prompt]);
        return args;
    }

    internal static List<string> BuildCodexResumeArguments(WorkerEnvironment environment, string workingDirectory, string threadId)
    {
        var args = new List<string> { "-y", "@openai/codex", "exec" };
        args.AddRange(["-c", $"projects={{{WorkerExtensions.QuoteToml(workingDirectory)}={{trust_level=\"untrusted\"}}}}"]);
        if (!string.IsNullOrWhiteSpace(environment.Model))
        {
            args.Add("-m");
            args.Add(environment.Model);
        }

        args.AddRange([
            "-C", workingDirectory,
            "--skip-git-repo-check",
            "--json",
            "--dangerously-bypass-approvals-and-sandbox",
            "resume",
            threadId,
            "The Formicae execution deadline is approaching. Stop starting new work. Make the current worktree internally coherent, preserve useful diagnostics, and return promptly. Do not run long tests and do not commit or push; the worker will checkpoint the worktree."
        ]);
        return args;
    }

    private static string BuildDeadlineInstructions(WorkerDeadlinePolicy deadline)
        => $"This Formicae run has a hard execution deadline of {(int)deadline.HardTimeout.TotalSeconds} seconds. " +
           $"At {(int)deadline.SoftTimeout.TotalSeconds} seconds Formicae may interrupt and resume this session with a checkpoint instruction. " +
           "Keep changes coherent throughout the run. Formicae owns the final commit and push.";

    internal static string? TryReadCodexThreadId(string line)
    {
        if (!line.StartsWith('{')) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return root.TryGetProperty("type", out var type)
                && string.Equals(type.GetString(), "thread.started", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("thread_id", out var threadId)
                ? threadId.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<int> CheckoutRepositoryAsync(WorkerEnvironment environment, WorkerReporter reporter, CancellationToken cancellationToken)
    {
        var repositoryUrl = BuildAuthenticatedRepositoryUrl(environment.RepositoryUrl, environment.GitAccessToken);
        var cloneExit = await RunProcessAsync("git", ["clone", repositoryUrl, RepositoryDirectory], null, reporter, cancellationToken, environment.GitAccessToken);
        if (cloneExit != 0)
        {
            return cloneExit;
        }

        return await RunProcessAsync("git", ["checkout", environment.Branch], RepositoryDirectory, reporter, cancellationToken, environment.GitAccessToken);
    }

    private static string BuildAuthenticatedRepositoryUrl(string repositoryUrl, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)
            || !Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return repositoryUrl;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = "x-access-token",
            Password = token
        };
        return builder.Uri.ToString();
    }

    private static async Task<bool> WaitForDockerAsync(WorkerReporter reporter, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        await reporter.ReportAsync("worker", "Waiting for the pod-local Docker daemon.", cancellationToken);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var startInfo = new ProcessStartInfo("docker")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("info");
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Docker CLI.");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode == 0)
            {
                await reporter.ReportAsync("worker", "Pod-local Docker daemon is ready.", cancellationToken);
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        await reporter.ReportAsync("worker-error", "Pod-local Docker daemon did not become ready within 60 seconds.", cancellationToken);
        return false;
    }

    private static string? ReadCodexAuth()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? "/tmp/codex-home", "auth.json");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    internal static async Task<int> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        WorkerReporter reporter,
        CancellationToken cancellationToken,
        string? redact = null,
        TaskCompletionSource<Process>? processStarted = null,
        Action<string>? stdoutObserver = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start process '{fileName}'.");
        processStarted?.TrySetResult(process);
        await reporter.ReportAsync("worker", $"Running {fileName} {string.Join(' ', arguments.Select(arg => Redact(arg, redact)))}", cancellationToken);
        var stdout = PumpAsync(process.StandardOutput, "stdout", reporter, cancellationToken, redact, stdoutObserver);
        var stderr = PumpAsync(process.StandardError, "stderr", reporter, cancellationToken, redact, null);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await InterruptProcessAsync(process);
            try { await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            throw;
        }
        return process.ExitCode;
    }

    private static async Task<int> RunProcessForDurationAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        WorkerReporter reporter,
        TimeSpan timeout,
        TimeProvider timeProvider,
        string? redact)
    {
        var processStarted = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processTask = RunProcessAsync(
            fileName,
            arguments,
            workingDirectory,
            reporter,
            CancellationToken.None,
            redact,
            processStarted);
        await Task.WhenAny(processTask, Task.Delay(timeout, timeProvider, CancellationToken.None));
        if (processTask.IsCompleted)
        {
            return await processTask;
        }

        await reporter.ReportAsync("worker-checkpoint", "Checkpoint finalization time expired; preserving the current worktree.", CancellationToken.None);
        if (processStarted.Task.IsCompletedSuccessfully)
        {
            await InterruptProcessAsync(processStarted.Task.Result);
        }
        await IgnoreProcessFailureAsync(processTask);
        return CheckpointExitCode;
    }

    private static async Task InterruptProcessAsync(Process process)
    {
        if (process.HasExited) return;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            return;
        }
        await process.WaitForExitAsync();
    }

    private static async Task IgnoreProcessFailureAsync(Task<int> processTask)
    {
        try
        {
            await processTask;
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
        {
        }
    }

    private static Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.CompletedTask;
        if (!cancellationToken.CanBeCanceled) return Task.Delay(Timeout.InfiniteTimeSpan);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => completion.TrySetResult());
        return completion.Task;
    }

    internal static async Task<(int ExitCode, string Output)> CaptureProcessAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start process '{fileName}'.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(output, error);
            return (process.ExitCode, await output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await InterruptProcessAsync(process);
            try { await Task.WhenAll(output, error); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            throw;
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        string stream,
        WorkerReporter reporter,
        CancellationToken cancellationToken,
        string? redact,
        Action<string>? observer)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var sanitized = reporter.Sanitize(Redact(line, redact));
            observer?.Invoke(sanitized);
            await reporter.ReportAsync(stream, sanitized, cancellationToken);
        }
    }

    private static string Redact(string value, string? secret)
        => string.IsNullOrWhiteSpace(secret) ? value : value.Replace(secret, "***", StringComparison.Ordinal);
}

internal sealed record WorkerDeadlinePolicy(
    TimeSpan HardTimeout,
    TimeSpan SoftTimeout,
    TimeSpan FinalizationTimeout,
    TimeSpan CheckpointTimeout)
{
    public static WorkerDeadlinePolicy? From(WorkerEnvironment environment)
    {
        if (!environment.CanCommitRepositoryChanges
            || environment.JobTimeoutSeconds is not { } timeoutSeconds
            || environment.CheckpointGraceSeconds <= 0)
        {
            return null;
        }

        timeoutSeconds = Math.Max(1, timeoutSeconds);
        var graceSeconds = Math.Clamp(environment.CheckpointGraceSeconds, 1, Math.Max(1, timeoutSeconds - 1));
        var finalizationSeconds = Math.Max(1, graceSeconds / 2);
        return new WorkerDeadlinePolicy(
            TimeSpan.FromSeconds(timeoutSeconds),
            TimeSpan.FromSeconds(timeoutSeconds - graceSeconds),
            TimeSpan.FromSeconds(finalizationSeconds),
            TimeSpan.FromSeconds(Math.Max(1, graceSeconds - finalizationSeconds)));
    }
}

internal sealed record CommitResult(int ExitCode, string? CommitSha, bool Changed);

internal sealed record WorkerCheckpointResult(
    string Type,
    string Branch,
    string? CommitSha,
    bool Changed,
    bool Pushed,
    string Reason);

internal static class CodexWorkspace
{
    public static void Prepare(bool requiresBrowser) => Prepare(requiresBrowser, []);

    public static void Prepare(bool requiresBrowser, IReadOnlyList<EnvironmentMcpServer> servers)
    {
        var targetHome = Environment.GetEnvironmentVariable("CODEX_HOME") ?? "/tmp/codex-home";
        var sourceDirectory = Environment.GetEnvironmentVariable("FORMICAE_CODEX_AUTH_MOUNT_PATH") ?? "/root/.codex";
        var sourceFileName = Environment.GetEnvironmentVariable("FORMICAE_CODEX_AUTH_FILE_NAME") ?? "auth.json";
        Directory.CreateDirectory(targetHome);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(targetHome, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        CopyIfPresent(Path.Combine(sourceDirectory, sourceFileName), Path.Combine(targetHome, "auth.json"));
        var auth = Path.Combine(targetHome, "auth.json");
        if (File.Exists(auth)) WorkerExtensions.SecureFile(auth);
        var config = Path.Combine(targetHome, "config.toml");
        File.WriteAllText(config, WorkerExtensions.BuildCodexConfiguration(servers, requiresBrowser));
        WorkerExtensions.SecureFile(config);
    }

    private static void CopyIfPresent(string source, string target)
    {
        if (File.Exists(source) && !string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.Ordinal))
        {
            File.Copy(source, target, overwrite: true);
        }
    }
}

internal sealed class WorkerReporter : IDisposable
{
    private readonly Uri? callbackUrl;
    private readonly string? callbackSecret;
    private readonly Guid workflowId;
    private readonly string taskKind;
    private readonly string externalId;
    private readonly Guid? attemptId;
    private readonly HttpClient http;
    private readonly TextWriter? runtimeWriter;
    private readonly string[] secrets;
    private readonly StringBuilder scriptOutput = new();
    private int scriptOutputBytes;
    private bool scriptOutputTruncated;
    private string? scriptFailureReason;
    private readonly Channel<WorkerAgentMessage> pending;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task delivery;
    private long sequence;
    private long dropped;
    private bool disposed;
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    public WorkerReporter(Uri? callbackUrl, string? callbackSecret, Guid workflowId, string taskKind, string externalId,
        Guid? attemptId = null, HttpMessageHandler? handler = null, int capacity = 512, IEnumerable<string>? knownSecrets = null, TextWriter? runtimeWriter = null)
    {
        this.runtimeWriter = runtimeWriter;
        this.callbackUrl = callbackUrl; this.callbackSecret = callbackSecret; this.workflowId = workflowId;
        this.taskKind = taskKind; this.externalId = externalId; this.attemptId = attemptId;
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(3);
        secrets = (knownSecrets ?? LoadKnownSecrets()).Append(callbackSecret ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value)).SelectMany(SecretVariants).Distinct().OrderByDescending(value => value.Length).ToArray();
        pending = Channel.CreateBounded<WorkerAgentMessage>(new BoundedChannelOptions(Math.Max(1, capacity))
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        delivery = DeliverAsync(shutdown.Token);
    }

    private static IEnumerable<string> SecretVariants(string value)
    {
        yield return value;
        yield return JsonSerializer.Serialize(value)[1..^1];
        yield return Uri.EscapeDataString(value);
        yield return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        foreach (var line in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            if (line.Length >= 4) yield return line;
    }

    public string Sanitize(string text)
    {
        foreach (var secret in secrets) text = text.Replace(secret, "***", StringComparison.Ordinal);
        return text;
    }

    private static IEnumerable<string> LoadKnownSecrets()
    {
        var values = new List<string>();
        foreach (var name in new[] { "FORMICAE_GIT_ACCESS_TOKEN", "FORMICAE_WORKER_CALLBACK_SECRET", "LLM_API_KEY", "OPENAI_API_KEY", "ANTHROPIC_API_KEY" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) values.Add(value);
        foreach (var name in JsonSerializer.Deserialize<string[]>(Environment.GetEnvironmentVariable("FORMICAE_SECRET_ENVIRONMENT_NAMES") ?? "[]") ?? [])
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) values.Add(value);
        try
        {
            var directory = Environment.GetEnvironmentVariable("FORMICAE_CODEX_AUTH_MOUNT_PATH") ?? "/root/.codex";
            var file = Environment.GetEnvironmentVariable("FORMICAE_CODEX_AUTH_FILE_NAME") ?? "auth.json";
            if (File.Exists(Path.Combine(directory, file)))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, file)));
                void Visit(JsonElement element, string name = "")
                {
                    if (element.ValueKind == JsonValueKind.Object)
                        foreach (var property in element.EnumerateObject()) Visit(property.Value, property.Name);
                    else if (element.ValueKind == JsonValueKind.String && (name.Contains("token", StringComparison.OrdinalIgnoreCase) || name.Contains("key", StringComparison.OrdinalIgnoreCase)))
                        if (element.GetString() is { Length: > 0 } secret) values.Add(secret);
                }
                Visit(json.RootElement);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
        return values;
    }

    public Task ReportAsync(string stream, string line, CancellationToken cancellationToken = default)
        => ReportLineAsync(stream, line, sanitize: true, cancellationToken);

    public void CaptureScriptOutput(string line)
    {
        if (scriptOutputTruncated) return;
        line = Sanitize(line);
        var lineBytes = Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);
        if (scriptOutputBytes + lineBytes > 262144)
        {
            scriptOutput.AppendLine("[Script output truncated: 256 KiB limit exceeded.]");
            scriptOutputTruncated = true;
            return;
        }
        scriptOutput.AppendLine(line);
        scriptOutputBytes += lineBytes;
    }

    public Task ReportScriptResultAsync(int exitCode) => ReportLineAsync("worker",
        JsonSerializer.Serialize(new { formicaeScriptResult = new { exitCode, output = scriptOutput.ToString(), truncated = scriptOutputTruncated, failureReason = scriptFailureReason } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
        sanitize: false, CancellationToken.None);

    private Task ReportLineAsync(string stream, string line, bool sanitize, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(line)) return Task.CompletedTask;
        var sanitized = sanitize ? Sanitize(line) : line;
        if (stream == "worker-error") scriptFailureReason = sanitized;
        // Keep complete supported agent final responses in runtime evidence; callbacks remain small.
        if (sanitized.Length > 1048576) sanitized = sanitized[..1048576] + "\n[Message truncated: runtime line limit exceeded]";
        var message = new WorkerAgentMessage(workflowId, taskKind, externalId, stream, sanitized,
            DateTimeOffset.UtcNow, Guid.NewGuid(), attemptId, Interlocked.Increment(ref sequence));
        WriteRuntime(message);
        if (message.Line.Length > 16000) message = message with { Line = message.Line[..16000] + "\n[Message truncated; full text retained in runtime logs]" };
        if (callbackUrl is not null && !pending.Writer.TryWrite(message))
        {
            if (Interlocked.Increment(ref dropped) == 1)
                WriteRuntime(new WorkerAgentMessage(workflowId, taskKind, externalId, "worker-error",
                    "[Live log delivery gap: callback queue is full; runtime logs retain the omitted messages.]",
                    DateTimeOffset.UtcNow, Guid.NewGuid(), attemptId, Interlocked.Increment(ref sequence)));
        }
        return Task.CompletedTask;
    }

    private void WriteRuntime(WorkerAgentMessage message)
    {
        var envelope = JsonSerializer.Serialize(new { formicaeLog = 1, data = message }, JsonOptions);
        if (runtimeWriter is not null) { lock (runtimeWriter) runtimeWriter.WriteLine(envelope); }
        else if (message.Stream is "stderr" or "worker-error") Console.Error.WriteLine(envelope);
        else Console.WriteLine(envelope);
    }

    private async Task DeliverAsync(CancellationToken token)
    {
        try
        {
            await foreach (var message in pending.Reader.ReadAllAsync(token))
            {
                var delivered = false;
                for (var retry = 0; retry < 3 && !delivered; retry++)
                {
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post, callbackUrl)
                        { Content = JsonContent.Create(message, options: JsonOptions) };
                        if (!string.IsNullOrWhiteSpace(callbackSecret)) request.Headers.Add("X-Formicae-Worker-Callback-Secret", callbackSecret);
                        using var response = await http.SendAsync(request, token);
                        delivered = response.IsSuccessStatusCode;
                        if (!delivered && (int)response.StatusCode is not (408 or 409 or 429 or >= 500)) break;
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested) { }
                    if (!delivered && retry < 2) await Task.Delay(TimeSpan.FromMilliseconds(100 * (retry + 1)), token);
                }
                if (!delivered)
                    WriteRuntime(new WorkerAgentMessage(workflowId, taskKind, externalId, "worker-error",
                        "[Live log delivery gap: callback failed; runtime logs retain message " + message.MessageId + ".]",
                        DateTimeOffset.UtcNow, Guid.NewGuid(), attemptId, Interlocked.Increment(ref sequence)));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public async Task FlushAsync()
    {
        pending.Writer.TryComplete();
        try { await delivery.WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (TimeoutException)
        {
            WriteRuntime(new WorkerAgentMessage(workflowId, taskKind, externalId, "worker-error",
                "[Live log delivery gap: shutdown flush timed out; remaining output is retained in runtime logs.]",
                DateTimeOffset.UtcNow, Guid.NewGuid(), attemptId, Interlocked.Increment(ref sequence)));
            shutdown.Cancel();
            await delivery;
        }
    }

    public async Task ReportCodexAuthAsync(string? aiSettingsId, string? codexAuthJson, CancellationToken cancellationToken = default)
    {
        if (callbackUrl is null || string.IsNullOrWhiteSpace(aiSettingsId) || string.IsNullOrWhiteSpace(codexAuthJson))
        {
            return;
        }

        try
        {
            var authUrl = new Uri(callbackUrl, "/api/worker/agent-auth");
            using var request = new HttpRequestMessage(HttpMethod.Post, authUrl)
            {
                Content = JsonContent.Create(new WorkerAgentAuthRefresh(workflowId, taskKind, externalId, aiSettingsId, codexAuthJson), options: JsonSerializerOptions.Web)
            };
            if (!string.IsNullOrWhiteSpace(callbackSecret))
            {
                request.Headers.Add("X-Formicae-Worker-Callback-Secret", callbackSecret);
            }

            await http.SendAsync(request, cancellationToken);
        }
        catch
        {
            // The next job can still run with the stored credentials; auth refresh persistence is best effort.
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; pending.Writer.TryComplete(); shutdown.Cancel(); http.Dispose();
    }
}

internal sealed record WorkerAgentMessage(Guid WorkflowId, string TaskKind, string ExternalId, string Stream, string Line, DateTimeOffset Timestamp, Guid? MessageId = null, Guid? ExecutionAttemptId = null, long? Sequence = null);
internal sealed record WorkerAgentAuthRefresh(Guid WorkflowId, string TaskKind, string ExternalId, string AiSettingsId, string CodexAuthJson);
