using System.Text;
using System.Text.Json;
using hhnl.Formicae.Application.Workflows;

internal static class WorkerExtensions
{
    internal static async Task<int> InstallToolsAsync(IReadOnlyList<EnvironmentToolInstall> tools, WorkerReporter reporter,
        TimeProvider timeProvider, CancellationToken token, string workspaceDirectory = "/workspace")
    {
        foreach (var tool in tools)
        {
            await reporter.ReportAsync("worker", $"Installing environment tool '{tool.Name}'.", token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(tool.TimeoutSeconds), timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            int exit;
            try { exit = await ExecuteScriptAsync(tool.Shell, tool.Script, workspaceDirectory, reporter, linked.Token); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
            {
                await reporter.ReportAsync("worker-error", $"Tool installation '{tool.Name}' exceeded its {tool.TimeoutSeconds}s deadline.", token);
                return 124;
            }
            if (exit != 0)
            {
                await reporter.ReportAsync("worker-error", $"Tool installation '{tool.Name}' failed with exit code {exit}; task execution was not started.", token);
                return exit;
            }
            await reporter.ReportAsync("worker", $"Environment tool '{tool.Name}' installed successfully.", token);
        }
        return 0;
    }

    internal static async Task<int> RunScriptAsync(WorkflowScriptSettings script, string directory, WorkerReporter reporter, CancellationToken token)
    {
        var exit = await ExecuteScriptAsync(script.Shell, script.Script, directory, reporter, token, reporter.CaptureScriptOutput);
        if (exit != 0) await reporter.ReportAsync("worker-error", $"Workflow script failed with exit code {exit}.", token);
        return exit;
    }

    private static async Task<int> ExecuteScriptAsync(string shell, string script, string directory, WorkerReporter reporter,
        CancellationToken token, Action<string>? output = null)
    {
        if (!WorkflowExecutionExtensions.ValidShell(shell)) throw new InvalidOperationException("Only sh and bash script shells are supported.");
        var path = Path.Combine(Path.GetTempPath(), "formicae-script-" + Guid.NewGuid().ToString("N") + ".sh");
        try
        {
            await File.WriteAllTextAsync(path, script, new UTF8Encoding(false), token);
            SecureFile(path);
            return await WorkerCommand.RunProcessAsync(shell, [path], directory, reporter, token, stdoutObserver: output);
        }
        finally { File.Delete(path); }
    }

    internal static void ConfigureOpenHands(IReadOnlyList<EnvironmentMcpServer> servers, bool browser)
    {
        var directory = Path.Combine(Path.GetTempPath(), "formicae-openhands-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("OPENHANDS_PERSISTENCE_DIR", directory);
        var file = Path.Combine(directory, "mcp.json");
        File.WriteAllText(file, BuildOpenHandsConfiguration(servers, browser));
        SecureFile(file);
    }

    internal static string BuildOpenHandsConfiguration(IReadOnlyList<EnvironmentMcpServer> servers, bool browser)
    {
        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var server in servers)
        {
            if (server.Transport == "stdio")
                entries.Add(server.Name, new { transport = "stdio", command = server.Command, args = server.Arguments ?? [],
                    env = (server.EnvironmentVariables ?? new Dictionary<string, string>()).ToDictionary(pair => pair.Key, pair => RequiredSecret(pair.Value)), enabled = true });
            else
            {
                var headers = (server.HeaderEnvironmentVariables ?? new Dictionary<string, string>()).ToDictionary(pair => pair.Key, pair => RequiredSecret(pair.Value), StringComparer.OrdinalIgnoreCase);
                if (server.BearerTokenEnvironmentVariable is { } bearer) headers.Add("Authorization", "Bearer " + RequiredSecret(bearer));
                entries.Add(server.Name, new { transport = "http", url = server.Url, headers, enabled = true });
            }
        }
        if (browser) entries.Add("playwright", new { transport = "stdio", command = "playwright-mcp", args = BrowserArguments, enabled = true });
        return JsonSerializer.Serialize(new { mcpServers = entries }, JsonSerializerOptions.Web);
    }

    internal static string BuildCodexConfiguration(IReadOnlyList<EnvironmentMcpServer> servers, bool browser)
    {
        static string Quote(string value) => QuoteToml(value);
        var text = new StringBuilder("# Task-owned Formicae configuration. Credentials are never logged.\n[mcp_servers]\n");
        foreach (var server in servers)
        {
            text.AppendLine($"[mcp_servers.{Quote(server.Name)}]");
            if (server.Transport == "stdio")
            {
                text.AppendLine("command = " + Quote(server.Command!));
                text.AppendLine("args = [" + string.Join(", ", (server.Arguments ?? []).Select(Quote)) + "]");
                text.AppendLine("required = true");
                if (server.EnvironmentVariables is { Count: > 0 })
                {
                    text.AppendLine($"[mcp_servers.{Quote(server.Name)}.env]");
                    foreach (var (name, alias) in server.EnvironmentVariables) text.AppendLine(Quote(name) + " = " + Quote(RequiredSecret(alias)));
                }
            }
            else
            {
                text.AppendLine("url = " + Quote(server.Url!));
                text.AppendLine("required = true");
                if (server.BearerTokenEnvironmentVariable is { } bearer)
                {
                    RequiredSecret(bearer);
                    text.AppendLine("bearer_token_env_var = " + Quote(bearer));
                }
                if (server.HeaderEnvironmentVariables is { Count: > 0 })
                {
                    text.AppendLine($"[mcp_servers.{Quote(server.Name)}.env_http_headers]");
                    foreach (var (header, alias) in server.HeaderEnvironmentVariables)
                    { RequiredSecret(alias); text.AppendLine(Quote(header) + " = " + Quote(alias)); }
                }
            }
        }
        if (browser)
        {
            text.AppendLine("[mcp_servers.playwright]");
            text.AppendLine("command = \"playwright-mcp\"");
            text.AppendLine("args = [" + string.Join(", ", BrowserArguments.Select(Quote)) + "]");
        }
        return text.ToString();
    }

    internal static string QuoteToml(string value)
    {
        var text = new StringBuilder("\"");
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case 34: text.Append("\\\""); break;
                case 92: text.Append("\\\\"); break;
                case 8: text.Append("\\b"); break;
                case 9: text.Append("\\t"); break;
                case 10: text.Append("\\n"); break;
                case 12: text.Append("\\f"); break;
                case 13: text.Append("\\r"); break;
                default:
                    if (rune.Value < 32 || rune.Value == 127) text.Append("\\u" + rune.Value.ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    else text.Append(rune.ToString());
                    break;
            }
        }
        return text.Append('"').ToString();
    }

    private static readonly string[] BrowserArguments = ["--headless", "--browser", "chromium", "--no-sandbox",
        "--output-dir", "test-results/agent-browser", "--allowed-origins", "http://127.0.0.1:*;http://localhost:*", "--caps", "core,network,devtools"];

    internal static string RequiredSecret(string alias) => Environment.GetEnvironmentVariable(alias)
        ?? throw new InvalidOperationException($"Selected secret environment alias '{alias}' is missing in this worker.");

    internal static void SecureFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
