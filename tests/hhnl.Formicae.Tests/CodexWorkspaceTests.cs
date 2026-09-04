extern alias worker;

public sealed class CodexWorkspaceTests
{
    [Fact]
    public void BuildCodexArguments_adds_task_specific_developer_instructions()
    {
        var planArguments = worker::WorkerCommand.BuildCodexArguments(EnvironmentFor("Plan"), "/workspace/repo");
        var implementationArguments = worker::WorkerCommand.BuildCodexArguments(EnvironmentFor("Implement", 3600, 600), "/workspace/repo");

        var configIndex = planArguments.IndexOf("-c");
        Assert.True(configIndex >= 0);
        Assert.Contains("developer_instructions=", planArguments[configIndex + 1]);
        Assert.Contains("non-interactive Formicae planning run", planArguments[configIndex + 1]);
        Assert.Contains("do not ask the user questions", planArguments[configIndex + 1], StringComparison.OrdinalIgnoreCase);
        var implementationConfigIndex = implementationArguments.IndexOf("-c");
        Assert.True(implementationConfigIndex >= 0);
        Assert.Contains("hard execution deadline of 3600 seconds", implementationArguments[implementationConfigIndex + 1]);
        Assert.Contains("At 3000 seconds", implementationArguments[implementationConfigIndex + 1]);
        Assert.Contains("Formicae owns the final commit and push", implementationArguments[implementationConfigIndex + 1]);
    }

    [Fact]
    public void WorkerDeadlinePolicy_uses_soft_deadline_without_waiting()
    {
        var policy = worker::WorkerDeadlinePolicy.From(EnvironmentFor("Implement", 3600, 600));

        Assert.NotNull(policy);
        Assert.Equal(TimeSpan.FromMinutes(60), policy.HardTimeout);
        Assert.Equal(TimeSpan.FromMinutes(50), policy.SoftTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), policy.FinalizationTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), policy.CheckpointTimeout);
        Assert.Null(worker::WorkerDeadlinePolicy.From(EnvironmentFor("Plan", 3600, 600)));
    }

    [Fact]
    public void BuildCodexResumeArguments_targets_captured_session_with_checkpoint_prompt()
    {
        var arguments = worker::WorkerCommand.BuildCodexResumeArguments(
            EnvironmentFor("Implement", 3600, 600),
            "/workspace/repo",
            "11111111-2222-3333-4444-555555555555");

        var resumeIndex = arguments.IndexOf("resume");
        Assert.True(resumeIndex > 0);
        Assert.Equal("11111111-2222-3333-4444-555555555555", arguments[resumeIndex + 1]);
        Assert.Contains("Stop starting new work", arguments[^1]);
        Assert.Contains("worker will checkpoint", arguments[^1]);
    }

    [Theory]
    [InlineData("{\"type\":\"thread.started\",\"thread_id\":\"thread-123\"}", "thread-123")]
    [InlineData("{\"type\":\"item.completed\"}", null)]
    [InlineData("not-json", null)]
    public void TryReadCodexThreadId_handles_jsonl_events(string line, string? expected)
        => Assert.Equal(expected, worker::WorkerCommand.TryReadCodexThreadId(line));

    [Fact]
    public void Prepare_preserves_auth_and_existing_config_while_adding_browser_mcp()
    {
        var root = Path.Combine(Path.GetTempPath(), $"formicae-codex-{Guid.NewGuid():N}");
        var mounted = Path.Combine(root, "mounted");
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(mounted);
        File.WriteAllText(Path.Combine(mounted, "auth.json"), "{\"tokens\":\"preserved\"}");
        File.WriteAllText(Path.Combine(mounted, "config.toml"), "model = \"gpt-test\"\n");

        var originalHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var originalMount = Environment.GetEnvironmentVariable("FORMICAE_CODEX_AUTH_MOUNT_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", local);
            Environment.SetEnvironmentVariable("FORMICAE_CODEX_AUTH_MOUNT_PATH", mounted);

            worker::CodexWorkspace.Prepare(requiresBrowser: true);
            worker::CodexWorkspace.Prepare(requiresBrowser: true);

            Assert.Equal("{\"tokens\":\"preserved\"}", File.ReadAllText(Path.Combine(local, "auth.json")));
            var config = File.ReadAllText(Path.Combine(local, "config.toml"));
            Assert.Contains("model = \"gpt-test\"", config);
            Assert.Equal(1, config.Split("[mcp_servers.playwright]").Length - 1);
            Assert.Contains("playwright-mcp", config);
            Assert.Contains("test-results/agent-browser", config);
            Assert.Contains("http://127.0.0.1:*;http://localhost:*", config);
            Assert.Contains("core,network,devtools", config);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", originalHome);
            Environment.SetEnvironmentVariable("FORMICAE_CODEX_AUTH_MOUNT_PATH", originalMount);
            Directory.Delete(root, recursive: true);
        }
    }

    private static worker::WorkerEnvironment EnvironmentFor(string taskKind, int? timeoutSeconds = null, int checkpointGraceSeconds = 0)
        => new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            taskKind,
            "https://github.com/acme/widgets",
            "main",
            "Do the work",
            null,
            "CodexSubscription",
            "worker-test",
            null,
            null,
            null,
            null,
            "/workspace/formicae/context",
            null,
            false,
            false,
            timeoutSeconds,
            checkpointGraceSeconds);
}
