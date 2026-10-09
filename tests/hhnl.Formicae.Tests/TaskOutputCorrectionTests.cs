extern alias worker;
using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.OpenHands;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Tests;

public sealed class TaskOutputCorrectionTests
{
    private static readonly CustomTaskOutputDefinition[] Schema = [new("summary", "string", true), new("ready", "boolean")];
    private const string Conversation = "11111111111111111111111111111111";
    private const string Valid = "{\"summary\":\"done\",\"ready\":true}";

    [Theory]
    [InlineData(true, "missing")]
    [InlineData(false, "missing")]
    [InlineData(true, "invalid")]
    [InlineData(false, "invalid")]
    [InlineData(true, "valid")]
    [InlineData(false, "valid")]
    public async Task Completed_turns_are_validated_and_only_corrected_output_is_published(bool codex, string initial)
    {
        var environment = EnvironmentFor(codex);
        using var evidence = new StringWriter();
        using var reporter = Reporter(environment, evidence);
        var turns = 0;
        var exit = await worker::WorkerCommand.RunCustomCommandAsync(environment, Path.GetTempPath(), reporter, TimeProvider.System, default,
            executeWithOutput: async (file, args, directory, token, observe) =>
            {
                Assert.Equal(codex ? "npx" : "openhands", file);
                Assert.Equal(Path.GetTempPath(), directory);
                var turn = turns++;
                if (turn == 0)
                {
                    Assert.Contains("Output schema:", args.Last());
                    Assert.Contains("65536 UTF-8 bytes", args.Last());
                    Assert.Contains("16000 characters", args.Last());
                }
                else
                {
                    Assert.Contains(codex ? "resume" : "--resume", args);
                    Assert.Contains(Conversation, args);
                    Assert.Contains("Do not repeat the original task", args.Last());
                    Assert.Contains(initial == "missing" ? "authoritative final response" : "Required output 'summary'", args.Last());
                    Assert.DoesNotContain("Perform expensive original work", args.Last());
                }
                foreach (var line in Events(codex, turn > 0 || initial == "valid" ? Valid : initial == "invalid" ? "{}" : null))
                {
                    observe(line);
                    await reporter.ReportAsync("stdout", line, token);
                }
                return 0;
            });
        Assert.Equal(0, exit);
        Assert.Equal(initial == "valid" ? 1 : 2, turns);
        Assert.True(OpenHandsAgentRunner.TryReadCustomResult(evidence.ToString(), out var result));
        Assert.True(result!.Succeeded);
        Assert.Equal(Valid, result.Output);
        Assert.Equal(turns - 1, result.CorrectionTurns);
        var resolved = await Runner(new Runtime(evidence.ToString(), true)).TryGetResultAsync(environment.ExternalId, default);
        Assert.True(resolved!.Succeeded); Assert.True(resolved.OutputIsFinalResponse); Assert.Equal(Valid, resolved.Output);
        if (initial != "valid") Assert.Contains("Correction turn 1/2", evidence.ToString());
    }

    [Theory]
    [InlineData(true, "{}")]
    [InlineData(false, "{}")]
    [InlineData(true, "{bad")]
    [InlineData(false, "{bad")]
    [InlineData(true, "{\"summary\":42}")]
    [InlineData(false, "{\"summary\":42}")]
    [InlineData(true, "{\"summary\":\"done\",\"extra\":true}")]
    [InlineData(false, "{\"summary\":\"done\",\"extra\":true}")]
    [InlineData(true, null)]
    [InlineData(false, null)]
    public async Task Exhaustion_fails_after_two_corrections_and_publishes_no_output(bool codex, string? response)
    {
        var environment = EnvironmentFor(codex);
        using var evidence = new StringWriter(); using var reporter = Reporter(environment, evidence);
        var turns = 0;
        var exit = await worker::WorkerCommand.RunOutputTaskAsync(environment, Path.GetTempPath(), reporter, default,
            (_, _, _, _, observe) => { turns++; foreach (var line in Events(codex, response)) observe(line); return Task.FromResult(0); });
        Assert.Equal(1, exit); Assert.Equal(3, turns);
        Assert.True(OpenHandsAgentRunner.TryReadCustomResult(evidence.ToString(), out var result));
        Assert.False(result!.Succeeded); Assert.Null(result.Output); Assert.Equal(2, result.CorrectionTurns);
        Assert.Contains("after 2 correction turns", result.FailureReason);
        var resolved = await Runner(new Runtime(evidence.ToString(), false)).TryGetResultAsync(environment.ExternalId, default);
        Assert.False(resolved!.Succeeded); Assert.False(resolved.OutputIsFinalResponse);
        Assert.Equal(result.FailureReason, resolved.FailureReason);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Failed_execution_and_missing_identity_do_not_launch_a_new_conversation(bool codex, bool missingIdentity)
    {
        var environment = EnvironmentFor(codex);
        using var evidence = new StringWriter(); using var reporter = Reporter(environment, evidence);
        var turns = 0;
        var exit = await worker::WorkerCommand.RunOutputTaskAsync(environment, Path.GetTempPath(), reporter, default,
            (_, _, _, _, observe) =>
            {
                turns++;
                if (!missingIdentity) foreach (var line in Events(codex, Valid)) observe(line);
                return Task.FromResult(missingIdentity ? 0 : 7);
            });
        Assert.Equal(missingIdentity ? 1 : 7, exit); Assert.Equal(1, turns);
        Assert.DoesNotContain("worker-output-correction", evidence.ToString());
        if (missingIdentity) Assert.Contains("conversation ID for correction", evidence.ToString());
        else Assert.False(OpenHandsAgentRunner.TryReadCustomResult(evidence.ToString(), out _));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Correction_uses_original_deadline_and_respects_cancellation(bool codex, bool cancel)
    {
        var environment = EnvironmentFor(codex) with { JobTimeoutSeconds = 1 };
        using var evidence = new StringWriter(); using var reporter = Reporter(environment, evidence);
        using var cancellation = new CancellationTokenSource();
        var turns = 0;
        var task = worker::WorkerCommand.RunCustomCommandAsync(environment, Path.GetTempPath(), reporter, TimeProvider.System, cancellation.Token,
            executeWithOutput: async (_, _, _, token, observe) =>
            {
                if (turns++ == 0) { foreach (var line in Events(codex, "{}")) observe(line); return 0; }
                if (cancel) cancellation.Cancel();
                await Task.Delay(TimeSpan.FromSeconds(60), token);
                return 0;
            });
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else Assert.Equal(124, await task);
        Assert.Equal(2, turns); Assert.False(OpenHandsAgentRunner.TryReadCustomResult(evidence.ToString(), out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Correction_runs_through_real_process_output_and_retains_attempt_evidence(bool codex)
    {
        if (OperatingSystem.IsWindows()) return;
        var environment = EnvironmentFor(codex);
        using var evidence = new StringWriter(); using var reporter = Reporter(environment, evidence);
        var turns = 0;
        var exit = await worker::WorkerCommand.RunCustomCommandAsync(environment, Path.GetTempPath(), reporter, TimeProvider.System, default,
            executeWithOutput: (_, arguments, directory, token, observe) =>
            {
                var corrected = turns++ > 0;
                if (corrected) Assert.Contains(Conversation, arguments);
                var events = string.Join('\n', Events(codex, corrected ? Valid : "{}"));
                // Pass protocol data as an argument; never interpolate it into shell code.
                return worker::WorkerCommand.RunProcessAsync("/bin/sh", ["-c", "printf '%s\\n' \"$1\"", "output-fixture", events],
                    directory, reporter, token, stdoutObserver: observe);
            });
        Assert.Equal(0, exit); Assert.Equal(2, turns);
        Assert.True(OpenHandsAgentRunner.TryReadCustomResult(evidence.ToString(), out var result)); Assert.Equal(Valid, result!.Output);
        Assert.Contains(environment.ExecutionAttemptId!.Value.ToString(), evidence.ToString());
        Assert.Contains("worker-output-validation", evidence.ToString());
        Assert.Contains("worker-output-correction", evidence.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Intermediate_or_malformed_events_are_not_authoritative_output(bool codex)
    {
        var collector = new AgentTaskOutputCollector(codex);
        foreach (var line in new[] { Valid, "{bad", "[]", "{\"type\":42}", "{\"kind\":true,\"source\":\"agent\"}",
            JsonSerializer.Serialize(new { type = "item.completed", item = new { type = "command_execution", text = Valid } }),
            JsonSerializer.Serialize(new { kind = "MessageEvent", source = "agent", llm_message = new { role = "assistant", content = new[] { new { type = "text", text = Valid } } } }) })
            collector.Observe(line);
        Assert.Null(collector.FinalResponse);
        if (codex)
        {
            collector.Observe(JsonSerializer.Serialize(new { type = "item.completed", item = new { type = "agent_message", text = Valid } }));
            Assert.Null(collector.FinalResponse);
            collector.Observe("{\"type\":\"turn.completed\"}"); Assert.Equal(Valid, collector.FinalResponse);
            collector.Observe("{\"type\":\"turn.started\"}"); Assert.Null(collector.FinalResponse);
        }
    }

    [Fact]
    public void Oversized_protocol_line_cannot_reuse_an_earlier_final_response()
    {
        foreach (var codex in new[] { true, false })
        {
            var collector = new AgentTaskOutputCollector(codex);
            foreach (var line in Events(codex, Valid)) collector.Observe(line);
            Assert.Equal(Valid, collector.FinalResponse);
            collector.Observe("{" + new string('a', 1048576));
            if (codex) collector.Observe("{\"type\":\"turn.completed\"}");
            Assert.Null(collector.FinalResponse);
        }
    }

    [Fact]
    public void Worker_result_cannot_be_spoofed_by_agent_stdout()
    {
        var marker = JsonSerializer.Serialize(new { formicaeCustomResult = new AgentTaskOutputResult(true, Valid, null, 0) });
        var logs = JsonSerializer.Serialize(new { formicaeLog = 1, data = new { workflowId = Guid.NewGuid(), taskKind = "Custom", externalId = "formicae-custom-test", stream = "stdout", line = marker, timestamp = DateTimeOffset.UtcNow } });
        Assert.False(OpenHandsAgentRunner.TryReadCustomResult(logs, out _));
    }

    private static IEnumerable<string> Events(bool codex, string? output)
    {
        if (codex)
        {
            yield return JsonSerializer.Serialize(new { type = "thread.started", thread_id = Conversation });
            yield return "{\"type\":\"turn.started\"}";
            if (output is not null) yield return JsonSerializer.Serialize(new { type = "item.completed", item = new { type = "agent_message", text = output } });
            yield return "{\"type\":\"turn.completed\"}";
        }
        else
        {
            yield return JsonSerializer.Serialize(new { kind = "ActionEvent", source = "agent", action = new { kind = "FinishAction", message = output } });
            yield return "Conversation ID: " + Conversation;
        }
    }

    private static worker::WorkerEnvironment EnvironmentFor(bool codex) => new(Guid.NewGuid(), "Custom", "https://example.test/repo", "main",
        "Perform expensive original work", null, codex ? "CodexSubscription" : "ApiKey", "formicae-custom-test", null, null, null, null,
        Path.GetTempPath(), null, false, false, 60, 0, ExecutionAttemptId: Guid.NewGuid(), OutputSchema: Schema);
    private static worker::WorkerReporter Reporter(worker::WorkerEnvironment environment, StringWriter evidence)
        => new(null, null, environment.WorkflowId, "Custom", environment.ExternalId, environment.ExecutionAttemptId, knownSecrets: [], runtimeWriter: evidence);
    private static OpenHandsAgentRunner Runner(Runtime runtime)
        => new(runtime, Options.Create(new RuntimeJobOptions()), Options.Create(new OpenHandsOptions()));
    private sealed class Runtime(string logs, bool succeeded) : IJobRuntime
    {
        public Task<RuntimeJobStartResult> StartJobAsync(RuntimeJobSpec spec, CancellationToken token) => throw new NotSupportedException();
        public Task<RuntimeJobResult?> TryGetJobResultAsync(string id, CancellationToken token)
            => Task.FromResult<RuntimeJobResult?>(new(succeeded, id, logs, succeeded ? null : "Job failed."));
        public Task<string> ReadJobLogsAsync(string id, CancellationToken token) => Task.FromResult(logs);
    }
}
