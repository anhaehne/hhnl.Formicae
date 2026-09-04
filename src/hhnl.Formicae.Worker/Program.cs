using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

var environment = WorkerEnvironment.Load();
using var reporter = new WorkerReporter(environment.CallbackUrl, environment.CallbackSecret, environment.WorkflowId, environment.TaskKind, environment.ExternalId);

try
{
    await reporter.ReportAsync("worker", "Formicae worker started.");
    var exitCode = await WorkerCommand.RunAsync(environment, reporter, CancellationToken.None);
    await reporter.ReportAsync("worker", $"Formicae worker finished with exit code {exitCode}.");
    return exitCode;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    await reporter.ReportAsync("worker-error", exception.ToString());
    return 1;
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
    bool RequiresNestedContainers)
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
            IsTrue("FORMICAE_REQUIRES_NESTED_CONTAINERS"));
    }

    public bool UsesCodexSubscription => string.Equals(AuthMethod, "CodexSubscription", StringComparison.OrdinalIgnoreCase);
    public bool IsCodexAuthSetup => TaskKind is "CodexAuthSetup" || string.Equals(AuthMethod, "CodexSubscriptionSetup", StringComparison.OrdinalIgnoreCase);
    public bool RequiresRepositoryCheckout => TaskKind is "Plan" or "Implement" or "AddressComments";
    public bool CanCommitRepositoryChanges => TaskKind is "Implement" or "AddressComments";

    private static string Required(string name)
        => Optional(name) ?? throw new InvalidOperationException($"Required environment variable '{name}' is missing.");

    private static string? Optional(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool IsTrue(string name)
        => bool.TryParse(Optional(name), out var value) && value;
}

internal static class WorkerCommand
{
    private const string WorkspaceDirectory = "/workspace";
    private const string RepositoryDirectory = "/workspace/repo";
    private const string NonInteractivePlanningInstructions =
        "This is a non-interactive Formicae planning run. Do not ask the user questions, request a mode change, " +
        "ask for the request to be resent, or invoke interactive shaping skills. Inspect the repository and " +
        "available product context directly, make conservative assumptions where needed, and return a complete, actionable plan.";

    public static async Task<int> RunAsync(WorkerEnvironment environment, WorkerReporter reporter, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(WorkspaceDirectory);
        if (environment.IsCodexAuthSetup)
        {
            return await RunCodexAuthSetupAsync(environment, reporter, cancellationToken);
        }

        if (environment.RequiresNestedContainers && !await WaitForDockerAsync(reporter, cancellationToken))
        {
            return 1;
        }

        var workingDirectory = WorkspaceDirectory;
        if (environment.RequiresRepositoryCheckout)
        {
            workingDirectory = RepositoryDirectory;
            var checkoutExit = await CheckoutRepositoryAsync(environment, reporter, cancellationToken);
            if (checkoutExit != 0)
            {
                return checkoutExit;
            }
        }

        if (environment.UsesCodexSubscription)
        {
            return await RunCodexAsync(environment, workingDirectory, reporter, cancellationToken);
        }

        return await RunProcessAsync(
            "openhands",
            ["--headless", "--json", "--override-with-envs", "-t", environment.Prompt],
            environment.RequiresRepositoryCheckout ? workingDirectory : null,
            reporter,
            cancellationToken);
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
    private static async Task<int> RunCodexAsync(WorkerEnvironment environment, string workingDirectory, WorkerReporter reporter, CancellationToken cancellationToken)
    {
        CodexWorkspace.Prepare(environment.RequiresBrowser);

        var args = BuildCodexArguments(environment, workingDirectory);
        var codexExit = await RunProcessAsync("npx", args, workingDirectory, reporter, cancellationToken, environment.GitAccessToken);
        await reporter.ReportCodexAuthAsync(environment.AiSettingsId, ReadCodexAuth(), cancellationToken);
        if (codexExit != 0 || !environment.CanCommitRepositoryChanges)
        {
            return codexExit;
        }

        foreach (var command in new[]
        {
            new[] { "config", "user.email", "formicae@example.invalid" },
            new[] { "config", "user.name", "Formicae Agent" }
        })
        {
            var exit = await RunProcessAsync("git", command, workingDirectory, reporter, cancellationToken, environment.GitAccessToken);
            if (exit != 0)
            {
                return exit;
            }
        }

        var statusOutput = await CaptureProcessAsync("git", ["status", "--porcelain"], workingDirectory, cancellationToken);
        if (string.IsNullOrWhiteSpace(statusOutput.Output))
        {
            await reporter.ReportAsync("worker", "Codex completed without uncommitted file changes.", cancellationToken);
            return 0;
        }

        var addExit = await RunProcessAsync("git", ["add", "-A"], workingDirectory, reporter, cancellationToken, environment.GitAccessToken);
        if (addExit != 0)
        {
            return addExit;
        }

        var subject = environment.TaskKind == "AddressComments"
            ? $"Address comments for Formicae workflow {environment.WorkflowId:N}"
            : $"Implement Formicae workflow {environment.WorkflowId:N}";
        var commitExit = await RunProcessAsync("git", ["commit", "-m", subject], workingDirectory, reporter, cancellationToken, environment.GitAccessToken);
        if (commitExit != 0)
        {
            return commitExit;
        }

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
                return remoteExit;
            }
        }

        return await RunProcessAsync("git", ["push", "origin", environment.Branch], workingDirectory, reporter, cancellationToken, environment.GitAccessToken);
    }

    internal static List<string> BuildCodexArguments(WorkerEnvironment environment, string workingDirectory)
    {
        var args = new List<string> { "-y", "@openai/codex", "exec" };
        if (environment.TaskKind == "Plan")
        {
            args.AddRange(["-c", $"developer_instructions={NonInteractivePlanningInstructions}"]);
        }

        if (!string.IsNullOrWhiteSpace(environment.Model))
        {
            args.Add("-m");
            args.Add(environment.Model);
        }

        args.AddRange(["-C", workingDirectory, "--skip-git-repo-check", "--json", "--dangerously-bypass-approvals-and-sandbox", environment.Prompt]);
        return args;
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

    private static async Task<int> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        WorkerReporter reporter,
        CancellationToken cancellationToken,
        string? redact = null)
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

        await reporter.ReportAsync("worker", $"Running {fileName} {string.Join(' ', arguments.Select(arg => Redact(arg, redact)))}", cancellationToken);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start process '{fileName}'.");
        var stdout = PumpAsync(process.StandardOutput, "stdout", reporter, cancellationToken, redact);
        var stderr = PumpAsync(process.StandardError, "stderr", reporter, cancellationToken, redact);
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode;
    }

    private static async Task<(int ExitCode, string Output)> CaptureProcessAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
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
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, output);
    }

    private static async Task PumpAsync(StreamReader reader, string stream, WorkerReporter reporter, CancellationToken cancellationToken, string? redact)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var sanitized = Redact(line, redact);
            if (stream == "stderr")
            {
                Console.Error.WriteLine(sanitized);
            }
            else
            {
                Console.WriteLine(sanitized);
            }

            await reporter.ReportAsync(stream, sanitized, cancellationToken);
        }
    }

    private static string Redact(string value, string? secret)
        => string.IsNullOrWhiteSpace(secret) ? value : value.Replace(secret, "***", StringComparison.Ordinal);
}

internal static class CodexWorkspace
{
    public static void Prepare(bool requiresBrowser)
    {
        var targetHome = Environment.GetEnvironmentVariable("CODEX_HOME") ?? "/tmp/codex-home";
        var sourceDirectory = Environment.GetEnvironmentVariable("FORMICAE_CODEX_AUTH_MOUNT_PATH") ?? "/root/.codex";
        var sourceFileName = Environment.GetEnvironmentVariable("FORMICAE_CODEX_AUTH_FILE_NAME") ?? "auth.json";
        Directory.CreateDirectory(targetHome);

        CopyIfPresent(Path.Combine(sourceDirectory, sourceFileName), Path.Combine(targetHome, "auth.json"));
        var localConfig = Path.Combine(targetHome, "config.toml");
        CopyIfPresent(Path.Combine(sourceDirectory, "config.toml"), localConfig);

        if (!requiresBrowser)
        {
            return;
        }

        var existing = File.Exists(localConfig) ? File.ReadAllText(localConfig) : string.Empty;
        if (existing.Contains("[mcp_servers.playwright]", StringComparison.Ordinal))
        {
            return;
        }

        var separator = string.IsNullOrWhiteSpace(existing) || existing.EndsWith('\n') ? string.Empty : Environment.NewLine;
        var lines = new[]
        {
            "[mcp_servers.playwright]",
            "command = \"playwright-mcp\"",
            "args = [\"--headless\", \"--browser\", \"chromium\", \"--no-sandbox\", \"--output-dir\", \"test-results/agent-browser\", \"--allowed-origins\", \"http://127.0.0.1:*;http://localhost:*\", \"--caps\", \"core,network,devtools\"]"
        };
        File.AppendAllText(localConfig, separator + string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private static void CopyIfPresent(string source, string target)
    {
        if (File.Exists(source) && !string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.Ordinal))
        {
            File.Copy(source, target, overwrite: true);
        }
    }
}

internal sealed class WorkerReporter(Uri? callbackUrl, string? callbackSecret, Guid workflowId, string taskKind, string externalId) : IDisposable
{
    private readonly HttpClient http = new();

    public async Task ReportAsync(string stream, string line, CancellationToken cancellationToken = default)
    {
        if (callbackUrl is null || string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, callbackUrl)
            {
                Content = JsonContent.Create(new WorkerAgentMessage(workflowId, taskKind, externalId, stream, line, DateTimeOffset.UtcNow), options: JsonSerializerOptions.Web)
            };
            if (!string.IsNullOrWhiteSpace(callbackSecret))
            {
                request.Headers.Add("X-Formicae-Worker-Callback-Secret", callbackSecret);
            }

            await http.SendAsync(request, cancellationToken);
        }
        catch
        {
            // Kubernetes logs remain the durable fallback if the live callback is temporarily unavailable.
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

    public void Dispose() => http.Dispose();
}

internal sealed record WorkerAgentMessage(Guid WorkflowId, string TaskKind, string ExternalId, string Stream, string Line, DateTimeOffset Timestamp);
internal sealed record WorkerAgentAuthRefresh(Guid WorkflowId, string TaskKind, string ExternalId, string AiSettingsId, string CodexAuthJson);
