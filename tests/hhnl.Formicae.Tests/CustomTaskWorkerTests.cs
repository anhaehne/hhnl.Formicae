extern alias worker;
using System.Diagnostics;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.OpenHands;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Tests;

public sealed class CustomTaskWorkerTests
{
    [Theory]
    [InlineData("CodexSubscription", "npx")]
    [InlineData("ApiKey", "openhands")]
    public async Task Both_cli_paths_run_in_scratch_and_enforce_deadline_without_scheduler_or_checkpoint(string auth, string expectedCommand)
    {
        var environment = EnvironmentFor(auth, 1);
        Assert.False(environment.RequiresRepositoryCheckout); Assert.False(environment.CanCommitRepositoryChanges);
        Assert.Null(worker::WorkerDeadlinePolicy.From(environment));
        using var reporter = new worker::WorkerReporter(null, null, environment.WorkflowId, "Custom", "custom-test");
        var stopwatch = Stopwatch.StartNew();
        var exit = await worker::WorkerCommand.RunCustomCommandAsync(environment, Path.GetTempPath(), reporter, TimeProvider.System, default,
            async (command, arguments, directory, token) =>
            {
                Assert.Equal(expectedCommand, command); Assert.Equal(Path.GetTempPath(), directory);
                Assert.Contains("Do custom work", arguments); Assert.DoesNotContain(arguments, arg => arg.Contains("checkpoint", StringComparison.OrdinalIgnoreCase));
                // A real child process exercises process-tree termination, not just token cancellation.
                return OperatingSystem.IsWindows()
                    ? await worker::WorkerCommand.RunProcessAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"], directory, reporter, token)
                    : await worker::WorkerCommand.RunProcessAsync("/bin/sh", ["-c", "sleep 60"], directory, reporter, token);
            });
        Assert.Equal(124, exit); Assert.InRange(stopwatch.Elapsed.TotalSeconds, 0.8, 15);
    }

    [Fact]
    public async Task Custom_shutdown_cancellation_is_not_reported_as_timeout()
    {
        var environment = EnvironmentFor("ApiKey", 60);
        using var reporter = new worker::WorkerReporter(null, null, environment.WorkflowId, "Custom", "custom-test");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker::WorkerCommand.RunCustomCommandAsync(environment,
            Path.GetTempPath(), reporter, TimeProvider.System, cancellation.Token,
            (_, _, _, token) => Task.FromCanceled<int>(token)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(3601)]
    public async Task Custom_runner_and_worker_reject_missing_or_out_of_bounds_deadline(int? timeout)
    {
        var runtime = new Runtime(); var runner = new OpenHandsAgentRunner(runtime, Options.Create(new RuntimeJobOptions()), Options.Create(new OpenHandsOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.StartAsync(TaskDefinition(timeout), default));
        Assert.Null(runtime.Spec);
        var environment = EnvironmentFor("ApiKey", timeout);
        using var reporter = new worker::WorkerReporter(null, null, environment.WorkflowId, "Custom", "custom-test");
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker::WorkerCommand.RunCustomCommandAsync(environment,
            Path.GetTempPath(), reporter, TimeProvider.System, default, (_, _, _, _) => throw new Exception("Must not execute")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1800)]
    [InlineData(3600)]
    public async Task Custom_runner_uses_durable_attempt_bounded_policy_without_repository_privileges(int timeout)
    {
        var runtime = new Runtime(); var runner = new OpenHandsAgentRunner(runtime, Options.Create(new RuntimeJobOptions()), Options.Create(new OpenHandsOptions()));
        await runner.StartAsync(TaskDefinition(timeout), default);
        var spec = runtime.Spec!;
        Assert.Equal(new RuntimeJobExecutionPolicy(timeout, 0), spec.ExecutionPolicy);
        Assert.False(spec.ExecutionRequirements!.RequiresBrowser); Assert.False(spec.ExecutionRequirements.RequiresNestedContainers);
        Assert.True(spec.ReuseExisting); Assert.False(spec.Environment.ContainsKey("FORMICAE_GIT_ACCESS_TOKEN"));
        Assert.Equal("Custom", spec.Environment["FORMICAE_TASK_KIND"]);
    }

    private static AgentTask TaskDefinition(int? timeout) => new(Guid.NewGuid(), TaskRunKind.Custom, "Do custom work", "https://example.test/repo", "main", null,
        ExecutionAttemptId: Guid.NewGuid(), TimeoutSeconds: timeout);
    private static worker::WorkerEnvironment EnvironmentFor(string auth, int? timeout) => new(Guid.NewGuid(), "Custom",
        "https://example.test/repo", "main", "Do custom work", null, auth, "custom-test", null, null, null, null,
        "/workspace/formicae/context", null, false, false, timeout, 0);
    private sealed class Runtime : IJobRuntime
    {
        public RuntimeJobSpec? Spec;
        public Task<RuntimeJobStartResult> StartJobAsync(RuntimeJobSpec spec, CancellationToken token)
        { Spec = spec; return Task.FromResult(new RuntimeJobStartResult(spec.Name)); }
        public Task<RuntimeJobResult?> TryGetJobResultAsync(string id, CancellationToken token) => Task.FromResult<RuntimeJobResult?>(null);
        public Task<string> ReadJobLogsAsync(string id, CancellationToken token) => Task.FromResult("");
    }
}
