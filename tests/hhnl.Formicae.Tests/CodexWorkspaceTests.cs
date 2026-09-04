extern alias worker;

public sealed class CodexWorkspaceTests
{
    [Fact]
    public void BuildCodexArguments_adds_non_interactive_developer_instructions_only_for_planning()
    {
        var planArguments = worker::WorkerCommand.BuildCodexArguments(EnvironmentFor("Plan"), "/workspace/repo");
        var implementationArguments = worker::WorkerCommand.BuildCodexArguments(EnvironmentFor("Implement"), "/workspace/repo");

        var configIndex = planArguments.IndexOf("-c");
        Assert.True(configIndex >= 0);
        Assert.Contains("developer_instructions=", planArguments[configIndex + 1]);
        Assert.Contains("non-interactive Formicae planning run", planArguments[configIndex + 1]);
        Assert.Contains("do not ask the user questions", planArguments[configIndex + 1], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-c", implementationArguments);
    }

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

    private static worker::WorkerEnvironment EnvironmentFor(string taskKind)
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
            false);
}
