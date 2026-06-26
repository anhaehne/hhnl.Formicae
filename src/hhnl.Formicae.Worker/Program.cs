using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

var environment = WorkerEnvironment.Load();
using var reporter = new WorkerReporter(environment.CallbackUrl, environment.WorkflowId, environment.TaskKind, environment.ExternalId);

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
    string ContextPath)
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
            Optional("FORMICAE_CONTEXT_PATH") ?? "/workspace/formicae/context");
    }

    public bool UsesCodexSubscription => string.Equals(AuthMethod, "CodexSubscription", StringComparison.OrdinalIgnoreCase);
    public bool RequiresRepositoryCheckout => TaskKind is "Implement" or "AddressComments";

    private static string Required(string name)
        => Optional(name) ?? throw new InvalidOperationException($"Required environment variable '{name}' is missing.");

    private static string? Optional(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

internal static class WorkerCommand
{
    public static async Task<int> RunAsync(WorkerEnvironment environment, WorkerReporter reporter, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory("/workspace");
        if (environment.UsesCodexSubscription)
        {
            return await RunCodexAsync(environment, reporter, cancellationToken);
        }

        return await RunProcessAsync("openhands", ["--headless", "--json", "--override-with-envs", "-t", environment.Prompt], null, reporter, cancellationToken);
    }

    private static async Task<int> RunCodexAsync(WorkerEnvironment environment, WorkerReporter reporter, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Environment.GetEnvironmentVariable("CODEX_HOME") ?? "/tmp/codex-home");
        CopyCodexAuthIfMounted();

        var workingDirectory = "/workspace";
        if (environment.RequiresRepositoryCheckout)
        {
            workingDirectory = "/workspace/repo";
            var cloneExit = await RunProcessAsync("git", ["clone", environment.RepositoryUrl, workingDirectory], null, reporter, cancellationToken);
            if (cloneExit != 0)
            {
                return cloneExit;
            }

            foreach (var command in new[]
            {
                new[] { "checkout", environment.Branch },
                new[] { "config", "user.email", "formicae@example.invalid" },
                new[] { "config", "user.name", "Formicae Agent" }
            })
            {
                var exit = await RunProcessAsync("git", command, workingDirectory, reporter, cancellationToken);
                if (exit != 0)
                {
                    return exit;
                }
            }
        }

        var args = new List<string> { "-y", "@openai/codex", "exec" };
        if (!string.IsNullOrWhiteSpace(environment.Model))
        {
            args.Add("-m");
            args.Add(environment.Model);
        }

        args.AddRange(["-C", workingDirectory, "--skip-git-repo-check", "--json", "--dangerously-bypass-approvals-and-sandbox", environment.Prompt]);
        var codexExit = await RunProcessAsync("npx", args, workingDirectory, reporter, cancellationToken);
        if (codexExit != 0 || !environment.RequiresRepositoryCheckout)
        {
            return codexExit;
        }

        var statusOutput = await CaptureProcessAsync("git", ["status", "--porcelain"], workingDirectory, cancellationToken);
        if (string.IsNullOrWhiteSpace(statusOutput.Output))
        {
            await reporter.ReportAsync("worker", "Codex completed without uncommitted file changes.", cancellationToken);
            return 0;
        }

        var addExit = await RunProcessAsync("git", ["add", "-A"], workingDirectory, reporter, cancellationToken);
        if (addExit != 0)
        {
            return addExit;
        }

        var subject = environment.TaskKind == "AddressComments"
            ? $"Address comments for Formicae workflow {environment.WorkflowId:N}"
            : $"Implement Formicae workflow {environment.WorkflowId:N}";
        var commitExit = await RunProcessAsync("git", ["commit", "-m", subject], workingDirectory, reporter, cancellationToken);
        if (commitExit != 0)
        {
            return commitExit;
        }

        return await RunProcessAsync("git", ["push", "origin", environment.Branch], workingDirectory, reporter, cancellationToken);
    }

    private static void CopyCodexAuthIfMounted()
    {
        var targetHome = Environment.GetEnvironmentVariable("CODEX_HOME") ?? "/tmp/codex-home";
        var source = "/root/.codex/auth.json";
        if (!File.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(targetHome);
        File.Copy(source, Path.Combine(targetHome, "auth.json"), overwrite: true);
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

internal sealed class WorkerReporter(Uri? callbackUrl, Guid workflowId, string taskKind, string externalId) : IDisposable
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
            await http.PostAsJsonAsync(callbackUrl, new WorkerAgentMessage(workflowId, taskKind, externalId, stream, line, DateTimeOffset.UtcNow), JsonSerializerOptions.Web, cancellationToken);
        }
        catch
        {
            // Kubernetes logs remain the durable fallback if the live callback is temporarily unavailable.
        }
    }

    public void Dispose() => http.Dispose();
}

internal sealed record WorkerAgentMessage(Guid WorkflowId, string TaskKind, string ExternalId, string Stream, string Line, DateTimeOffset Timestamp);
