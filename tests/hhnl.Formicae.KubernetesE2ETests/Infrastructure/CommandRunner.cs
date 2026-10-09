using System.Diagnostics;
using System.Text;

namespace hhnl.Formicae.KubernetesE2ETests.Infrastructure;

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => string.Join(Environment.NewLine, [StandardOutput, StandardError]).Trim();
}

internal static class CommandRunner
{
    public static async Task<CommandResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(key);
                }
                else
                {
                    startInfo.Environment[key] = value;
                }
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, entry) => { if (entry.Data is not null) lock (stdout) stdout.AppendLine(entry.Data); };
        process.ErrorDataReceived += (_, entry) => { if (entry.Data is not null) lock (stderr) stderr.AppendLine(entry.Data); };
        if (!process.Start()) throw new InvalidOperationException($"Failed to start {fileName}.");
        var elapsed = Stopwatch.StartNew();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return new CommandResult(process.ExitCode, Output(stdout), Output(stderr));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            // Allow redirected streams to drain after killing the local process tree.
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
            cancellationToken.ThrowIfCancellationRequested();
            var evidence = new CommandResult(-1, Output(stdout), Output(stderr)).CombinedOutput;
            if (evidence.Length > 16000) evidence = "[earlier output omitted]\n" + evidence[^16000..];
            throw new TimeoutException($"Command timed out after {elapsed.Elapsed.TotalSeconds:F1}s (limit {timeout.TotalSeconds:F1}s): "
                + $"{Format(fileName, arguments)}{Environment.NewLine}{evidence}");
        }
    }

    private static string Output(StringBuilder output) { lock (output) return output.ToString(); }

    public static async Task<CommandResult> RunRequiredAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(fileName, arguments, workingDirectory, timeout, environment, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Command failed ({result.ExitCode}): {Format(fileName, arguments)}{Environment.NewLine}{result.CombinedOutput}");
        }

        return result;
    }

    public static Process StartLongRunning(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(key);
                }
                else
                {
                    startInfo.Environment[key] = value;
                }
            }
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static string Format(string fileName, IEnumerable<string> arguments)
    {
        var builder = new StringBuilder(fileName);
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            builder.Append(argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument);
        }

        return builder.ToString();
    }
}
