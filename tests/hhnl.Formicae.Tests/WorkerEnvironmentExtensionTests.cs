extern alias worker;
using System.Text;
using System.Text.Json;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Tests;

[CollectionDefinition("Worker extension process environment", DisableParallelization = true)]
public sealed class WorkerExtensionEnvironmentCollection;

[Collection("Worker extension process environment")]
public sealed class WorkerEnvironmentExtensionTests
{
    [Theory]
    [InlineData("printf 'first\\nsecond\\n'", 0, "first\nsecond\n")]
    [InlineData("printf 'diagnostic\\n'; exit 17", 17, "diagnostic\n")]
    public async Task Real_script_preserves_stdout_and_actual_exit_code(string script, int expectedExit, string expectedOutput)
    {
        using var runtime = new StringWriter();
        using var reporter = Reporter(runtime);
        var exit = await worker::WorkerExtensions.RunScriptAsync(new(script), Path.GetTempPath(), reporter, default);
        await reporter.ReportScriptResultAsync(exit);
        var result = ScriptResult(runtime);
        Assert.Equal(expectedExit, exit);
        Assert.Equal(expectedExit, result.GetProperty("exitCode").GetInt32());
        Assert.Equal(expectedOutput, result.GetProperty("output").GetString());
        if (expectedExit != 0) Assert.Contains("exit code 17", result.GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task Real_script_deadline_kills_child_and_reports_124()
    {
        using var runtime = new StringWriter();
        using var reporter = Reporter(runtime);
        var environment = EnvironmentFor(new("sleep 30; printf 'should-not-run'", TimeoutSeconds: 1));
        var started = DateTimeOffset.UtcNow;
        var exit = await worker::WorkerCommand.RunAsync(environment, reporter, default);
        await reporter.ReportScriptResultAsync(exit);
        Assert.Equal(124, exit);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("should-not-run", runtime.ToString());
        Assert.Contains("deadline", ScriptResult(runtime).GetProperty("failureReason").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task External_cancellation_terminates_script_without_reclassifying_as_timeout()
    {
        using var runtime = new StringWriter(); using var reporter = Reporter(runtime);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker::WorkerExtensions.RunScriptAsync(new("sleep 30"),
            Path.GetTempPath(), reporter, cancellation.Token));
        Assert.DoesNotContain("deadline", runtime.ToString());
    }

    [Theory]
    [InlineData("printf 'install-visible\\n'", 0, true)]
    [InlineData("printf 'install-failed\\n'; exit 9", 9, false)]
    [InlineData("sleep 30", 124, false)]
    public async Task Bootstrap_logs_and_failure_control_whether_script_starts(string install, int expectedExit, bool startsScript)
    {
        using var runtime = new StringWriter(); using var reporter = Reporter(runtime);
        var configuration = new EnvironmentConfiguration { Tools = [new("fixture", install, TimeoutSeconds: 1)] };
        var exit = await worker::WorkerCommand.RunAsync(EnvironmentFor(new("printf 'script-visible\\n'", TimeoutSeconds: 10), configuration), reporter, default);
        Assert.Equal(expectedExit, exit);
        Assert.Contains("Installing environment tool 'fixture'", hhnl.Formicae.Infrastructure.OpenHands.OpenHandsAgentRunner.UnwrapRuntimeLogs(runtime.ToString()));
        Assert.Equal(startsScript, runtime.ToString().Contains("script-visible", StringComparison.Ordinal));
        if (startsScript) Assert.Contains("installed successfully", runtime.ToString());
        else Assert.Contains("worker-error", runtime.ToString());
    }

    [Fact]
    public async Task Selected_secret_is_masked_in_logs_and_structured_output_including_encoded_forms()
    {
        const string secret = "special-credential/+\"\\tail";
        var previous = Environment.GetEnvironmentVariable("EXTENSION_TEST_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("EXTENSION_TEST_TOKEN", secret);
            using var runtime = new StringWriter(); using var reporter = Reporter(runtime, [secret]);
            var exit = await worker::WorkerExtensions.RunScriptAsync(new("printf '%s\\n' \"$EXTENSION_TEST_TOKEN\""), Path.GetTempPath(), reporter, default);
            foreach (var encoded in new[] { JsonSerializer.Serialize(secret)[1..^1], Uri.EscapeDataString(secret), Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)) })
                await reporter.ReportAsync("stdout", encoded);
            await reporter.ReportScriptResultAsync(exit);
            Assert.Equal("***\n", ScriptResult(runtime).GetProperty("output").GetString());
            Assert.DoesNotContain(secret, runtime.ToString());
            Assert.DoesNotContain(Uri.EscapeDataString(secret), runtime.ToString());
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), runtime.ToString());
        }
        finally { Environment.SetEnvironmentVariable("EXTENSION_TEST_TOKEN", previous); }
    }

    [Fact]
    public async Task Script_output_bound_retains_valid_final_protocol_and_marks_truncation()
    {
        using var runtime = new StringWriter(); using var reporter = Reporter(runtime);
        for (var index = 0; index < 300; index++) reporter.CaptureScriptOutput(new string('x', 1024));
        await reporter.ReportScriptResultAsync(0);
        var result = ScriptResult(runtime);
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.InRange(Encoding.UTF8.GetByteCount(result.GetProperty("output").GetString()!), 260000, 262300);
        Assert.Contains("256 KiB", result.GetProperty("output").GetString());
    }

    [Fact]
    public void Native_mcp_configs_resolve_only_selected_aliases_and_codex_uses_whole_project_trust_override()
    {
        var previous = Environment.GetEnvironmentVariable("EXTENSION_TEST_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("EXTENSION_TEST_TOKEN", "selected-token");
            EnvironmentMcpServer[] servers = [new("stdio-service", Command: "fixture-cli", Arguments: ["--json"],
                EnvironmentVariables: new Dictionary<string, string> { ["SERVER_TOKEN"] = "EXTENSION_TEST_TOKEN" }),
                new("http-service", "http", Url: "https://mcp.example.test/service", BearerTokenEnvironmentVariable: "EXTENSION_TEST_TOKEN",
                    HeaderEnvironmentVariables: new Dictionary<string, string> { ["X-Token"] = "EXTENSION_TEST_TOKEN" })];
            using var openhands = JsonDocument.Parse(worker::WorkerExtensions.BuildOpenHandsConfiguration(servers, false));
            var entries = openhands.RootElement.GetProperty("mcpServers");
            Assert.Equal("selected-token", entries.GetProperty("stdio-service").GetProperty("env").GetProperty("SERVER_TOKEN").GetString());
            Assert.Equal("Bearer selected-token", entries.GetProperty("http-service").GetProperty("headers").GetProperty("Authorization").GetString());
            Assert.False(entries.TryGetProperty("playwright", out _));
            var codex = worker::WorkerExtensions.BuildCodexConfiguration(servers, false);
            Assert.Contains("bearer_token_env_var = \"EXTENSION_TEST_TOKEN\"", codex);
            Assert.Contains("[mcp_servers.\"http-service\".env_http_headers]", codex);
            Assert.Contains("\"SERVER_TOKEN\" = \"selected-token\"", codex);
            Assert.DoesNotContain("playwright", codex);
            var environment = EnvironmentFor(new("true")) with { TaskKind = "Custom", AuthMethod = "codex-subscription" };
            const string directory = "/workspace/repo";
            foreach (var arguments in new[] { worker::WorkerCommand.BuildCodexArguments(environment, directory),
                         worker::WorkerCommand.BuildCodexResumeArguments(environment, directory, "session") })
                Assert.Contains("projects={\"/workspace/repo\"={trust_level=\"untrusted\"}}", arguments);
        }
        finally { Environment.SetEnvironmentVariable("EXTENSION_TEST_TOKEN", previous); }
    }

    [Fact]
    public void Missing_mcp_secret_alias_fails_before_native_harness_launch()
    {
        const string alias = "FORMICAE_UNIT_MISSING_ALIAS_783291";
        Assert.Null(Environment.GetEnvironmentVariable(alias));
        EnvironmentMcpServer[] servers = [new("service", Command: "fixture", EnvironmentVariables: new Dictionary<string, string> { ["TOKEN"] = alias })];
        Assert.Throws<InvalidOperationException>(() => worker::WorkerExtensions.BuildCodexConfiguration(servers, false));
        Assert.Throws<InvalidOperationException>(() => worker::WorkerExtensions.BuildOpenHandsConfiguration(servers, false));
    }

    private static worker::WorkerReporter Reporter(StringWriter runtime, IEnumerable<string>? secrets = null)
        => new(null, null, Guid.NewGuid(), "Script", "formicae-script-test", knownSecrets: secrets ?? [], runtimeWriter: runtime);
    private static worker::WorkerEnvironment EnvironmentFor(WorkflowScriptSettings script, EnvironmentConfiguration? configuration = null)
        => new(Guid.NewGuid(), "Script", "repo", "branch", "script", null, "none", "formicae-script-test", null, null, null, null,
            "/workspace/formicae/context", null, false, false, script.TimeoutSeconds, 0, ExecutionConfiguration: configuration, Script: script);
    private static JsonElement ScriptResult(StringWriter runtime)
    {
        foreach (var line in runtime.ToString().Split('\n').Reverse())
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var envelope = JsonDocument.Parse(line);
            var text = envelope.RootElement.GetProperty("data").GetProperty("line").GetString()!;
            if (!text.StartsWith("{\"formicaeScriptResult\":", StringComparison.Ordinal)) continue;
            using var result = JsonDocument.Parse(text);
            return result.RootElement.GetProperty("formicaeScriptResult").Clone();
        }
        throw new InvalidOperationException("Script result not found.");
    }
}
