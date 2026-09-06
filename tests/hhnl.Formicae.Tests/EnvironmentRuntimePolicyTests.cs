extern alias worker;
using hhnl.Formicae.Infrastructure;

namespace hhnl.Formicae.Tests;

public sealed class EnvironmentRuntimePolicyTests
{
    [Theory]
    [InlineData(null, null, 900, 900, 0)]
    [InlineData(40, null, 900, 40, 0)]
    [InlineData(1000, null, 900, 900, 0)]
    [InlineData(1, 3600, 900, 1, 0)]
    [InlineData(200, 3600, 900, 200, 199)]
    [InlineData(3600, 43, 900, 43, 42)]
    [InlineData(null, 3600, 900, 3600, 600)]
    public void Shared_policy_caps_existing_timeout_and_bounds_checkpoint_grace(int? cap, int? explicitTimeout, int runtimeDefault, int expectedTimeout, int expectedGrace)
    {
        var spec = new RuntimeJobSpec("test", "test", new Dictionary<string, string>(), [],
            ExecutionPolicy: explicitTimeout.HasValue ? new(explicitTimeout.Value, 600) : null, TimeoutLimitSeconds: cap);
        Assert.Equal(new RuntimeJobExecutionPolicy(expectedTimeout, expectedGrace), RuntimeJobPolicyResolver.Resolve(spec, runtimeDefault));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3601)]
    public void Invalid_environment_caps_fail_before_runtime_creation(int cap)
        => Assert.Throws<InvalidOperationException>(() => RuntimeJobPolicyResolver.Resolve(
            new("test", "test", new Dictionary<string, string>(), [], TimeoutLimitSeconds: cap), 900));

    [Theory]
    [InlineData("CodexSubscription", "Plan", 0, true)]
    [InlineData("CodexSubscription", "Implement", 0, true)]
    [InlineData("CodexSubscription", "AddressComments", 0, true)]
    [InlineData("CodexSubscription", "Implement", 5, false)]
    [InlineData("ApiKey", "Plan", 0, true)]
    [InlineData("ApiKey", "Implement", 5, true)]
    [InlineData("ApiKey", "AddressComments", 5, true)]
    [InlineData("CodexSubscription", "Custom", 0, false)]
    public void Hard_deadline_selection_preserves_codex_checkpoint_and_custom_paths(string auth, string kind, int grace, bool hard)
    {
        var environment = EnvironmentFor(auth, kind, grace);
        Assert.Equal(hard, environment.RequiresHardEnvironmentDeadline);
        Assert.False((environment with { EnvironmentTimeoutLimit = false }).RequiresHardEnvironmentDeadline);
    }

    [Theory]
    [InlineData("CodexSubscription", "Plan", 0)]
    [InlineData("CodexSubscription", "Implement", 0)]
    [InlineData("CodexSubscription", "AddressComments", 0)]
    [InlineData("ApiKey", "Implement", 5)]
    [InlineData("ApiKey", "AddressComments", 5)]
    public async Task Capped_worker_terminates_real_process_without_scheduler_polling(string auth, string kind, int grace)
    {
        var environment = EnvironmentFor(auth, kind, grace); Assert.True(environment.RequiresHardEnvironmentDeadline);
        using var reporter = new worker::WorkerReporter(null, null, environment.WorkflowId, kind, "environment-test");
        var exit = await worker::WorkerCommand.RunWithHardDeadlineAsync(1, reporter, TimeProvider.System, default,
            token => OperatingSystem.IsWindows()
                ? worker::WorkerCommand.RunProcessAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"], Path.GetTempPath(), reporter, token)
                : worker::WorkerCommand.RunProcessAsync("/bin/sh", ["-c", "sleep 60"], Path.GetTempPath(), reporter, token));
        Assert.Equal(124, exit);
    }

    [Fact]
    public async Task Successful_capped_execution_preserves_normal_finalization_and_exit_code()
    {
        var environment = EnvironmentFor("CodexSubscription", "Implement", 0); var finalized = false;
        using var reporter = new worker::WorkerReporter(null, null, environment.WorkflowId, environment.TaskKind, "environment-test");
        var result = await worker::WorkerCommand.RunWithHardDeadlineAsync(1, reporter, TimeProvider.System, default,
            token => { token.ThrowIfCancellationRequested(); finalized = true; return Task.FromResult(7); });
        Assert.True(finalized); Assert.Equal(7, result);
    }

    [Fact]
    public async Task Deadline_cancels_and_reaps_captured_setup_or_finalization_process()
    {
        using var reporter = new worker::WorkerReporter(null, null, Guid.NewGuid(), "Implement", "environment-test");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var exit = await worker::WorkerCommand.RunWithHardDeadlineAsync(1, reporter, TimeProvider.System, default,
            async token => (await (OperatingSystem.IsWindows()
                ? worker::WorkerCommand.CaptureProcessAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"], Path.GetTempPath(), token)
                : worker::WorkerCommand.CaptureProcessAsync("/bin/sh", ["-c", "sleep 60"], Path.GetTempPath(), token))).ExitCode);
        Assert.Equal(124, exit); Assert.InRange(stopwatch.Elapsed.TotalSeconds, 0.8, 15);
    }

    [Fact]
    public async Task Captured_setup_process_drains_stderr_without_deadlocking_stdout()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await (OperatingSystem.IsWindows()
            ? worker::WorkerCommand.CaptureProcessAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Error.Write(('e' * 100000)); [Console]::Out.Write('done')"], Path.GetTempPath(), cancellation.Token)
            : worker::WorkerCommand.CaptureProcessAsync("/bin/sh", ["-c", "head -c 100000 /dev/zero >&2; printf done"], Path.GetTempPath(), cancellation.Token));
        Assert.Equal(0, result.ExitCode); Assert.Equal("done", result.Output);
    }

    private static worker::WorkerEnvironment EnvironmentFor(string auth, string kind, int grace) => new(Guid.NewGuid(), kind,
        "https://example.test/repo", "main", "Do work", null, auth, "environment-test", null, null, null, null,
        "/workspace/formicae/context", null, false, false, 1, grace, true);
}
