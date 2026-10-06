extern alias worker;
using System.Net;
using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Infrastructure.OpenHands;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Tests;

public sealed class WorkerLogDeliveryTests
{
    [Fact]
    public async Task Transient_http_failures_retry_identical_messages_and_redact_both_delivery_paths()
    {
        using var output = new StringWriter();
        var handler = new Handler([HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK]);
        var attempt = Guid.NewGuid();
        using var reporter = new worker::WorkerReporter(new Uri("http://localhost/callback"), "callback-secret", Guid.NewGuid(),
            "Plan", "worker", attempt, handler, knownSecrets: ["injected-git-secret", "model-secret"], runtimeWriter: output);
        await reporter.ReportAsync("stdout", "injected-git-secret model-secret callback-secret");
        await reporter.FlushAsync();
        Assert.Equal(2, handler.Payloads.Count);
        Assert.Equal(handler.Payloads[0], handler.Payloads[1]);
        var payload = JsonDocument.Parse(handler.Payloads[0]).RootElement;
        Assert.Equal(attempt, payload.GetProperty("executionAttemptId").GetGuid());
        Assert.Equal("*** *** ***", payload.GetProperty("line").GetString());
        Assert.DoesNotContain("injected-git-secret", output.ToString());
        Assert.DoesNotContain("model-secret", output.ToString());
        Assert.DoesNotContain("callback-secret", output.ToString());
        Assert.Contains(payload.GetProperty("messageId").GetString()!, output.ToString());
    }

    [Fact]
    public async Task Permanent_http_failure_is_not_retried_and_runtime_gap_is_visible()
    {
        using var output = new StringWriter();
        var handler = new Handler([HttpStatusCode.Forbidden]);
        using var reporter = new worker::WorkerReporter(new Uri("http://localhost/callback"), null, Guid.NewGuid(), "Plan", "worker",
            handler: handler, runtimeWriter: output);
        await reporter.ReportAsync("stderr", "warning"); await reporter.FlushAsync();
        Assert.Single(handler.Payloads); Assert.Contains("Live log delivery gap", output.ToString());
    }

    [Fact]
    public async Task Queue_overflow_and_long_lines_are_bounded_and_retained_in_runtime_evidence()
    {
        using var output = new StringWriter();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler([HttpStatusCode.OK], gate.Task);
        using var reporter = new worker::WorkerReporter(new Uri("http://localhost/callback"), null, Guid.NewGuid(), "Custom", "worker",
            handler: handler, capacity: 1, runtimeWriter: output);
        await reporter.ReportAsync("stdout", new string('x', 25000));
        for (var i = 0; i < 20; i++) await reporter.ReportAsync("stdout", "line-" + i);
        gate.SetResult(); await reporter.FlushAsync();
        Assert.Contains("callback queue is full", output.ToString());
        Assert.Contains(handler.Payloads, payload => payload.Contains("Message truncated", StringComparison.Ordinal));
        Assert.Contains("line-19", output.ToString()); Assert.Contains(new string('x', 25000), output.ToString());
    }

    [Fact]
    public async Task Runtime_recovery_reuses_callback_identity_and_keeps_authoritative_final_response()
    {
        using var output = new StringWriter();
        var workflowId = Guid.NewGuid(); var attempt = Guid.NewGuid();
        using var reporter = new worker::WorkerReporter(null, null, workflowId, "Custom", "worker", attempt, runtimeWriter: output);
        await reporter.ReportAsync("stdout", "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"final response\"}}");
        await reporter.FlushAsync();
        var runtime = new Runtime(output.ToString());
        var runner = new OpenHandsAgentRunner(runtime, Options.Create(new RuntimeJobOptions()), Options.Create(new OpenHandsOptions()));
        var log = Assert.Single(await runner.ReadLogsAsync("worker", default));
        var envelope = JsonDocument.Parse(output.ToString()).RootElement.GetProperty("data");
        Assert.Equal(envelope.GetProperty("messageId").GetGuid(), log.MessageId);
        Assert.Equal("stdout", log.Source); Assert.Equal(1, log.SourceSequence);
        Assert.Equal(envelope.GetProperty("timestamp").GetDateTimeOffset(), log.Timestamp);
        Assert.Equal("final response", OpenHandsAgentRunner.ExtractFinalResponse(output.ToString()));
    }

    [Fact]
    public async Task Builtin_stdout_is_durable_deduplicated_and_cannot_overwrite_terminal_output()
    {
        var store = new InMemoryWorkflowStore();
        var workflow = await store.CreateWorkflowAsync(new Workflow { IssueUrl = "issue", RepositoryUrl = "repo" }, default);
        var attempt = Guid.NewGuid();
        var run = await store.UpsertTaskRunAsync(new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.Plan,
            DefinitionStepId = "plan", ExecutionAttemptId = attempt, ExternalId = "worker", Status = TaskRunStatus.Running, Output = "authoritative" }, default);
        var service = new WorkerAgentMessageService(store);
        var message = new WorkerAgentMessageRequest(workflow.Id, "Plan", "worker", "stdout", "raw tool output", DateTimeOffset.UtcNow, Guid.NewGuid(), attempt, 1);
        Assert.True(await service.RecordAsync(message, default)); Assert.True(await service.RecordAsync(message, default));
        var log = Assert.Single(await store.ListLogsAsync(workflow.Id, default));
        Assert.Equal("raw tool output", log.Message); Assert.Equal(attempt, log.ExecutionAttemptId);
        Assert.Equal("authoritative", run.Output);
        run.ExecutionAttemptId = Guid.NewGuid(); run.ExternalId = "new-worker";
        await store.UpsertTaskRunAsync(run, default);
        Assert.False(await service.RecordAsync(message with { MessageId = Guid.NewGuid() }, default));
        Assert.Single(await store.ListLogsAsync(workflow.Id, default));
    }

    [Theory]
    [InlineData("{\"formicaeLog\":\"invalid\",\"data\":{}}")]
    [InlineData("{\"formicaeLog\":1,\"data\":0}")]
    [InlineData("[]")]
    public async Task Malformed_runtime_envelopes_remain_legacy_evidence_instead_of_breaking_completion(string raw)
    {
        var runner = new OpenHandsAgentRunner(new Runtime(raw), Options.Create(new RuntimeJobOptions()), Options.Create(new OpenHandsOptions()));
        var log = Assert.Single(await runner.ReadLogsAsync("worker", default));
        Assert.Equal("runtime", log.Source); Assert.Equal(raw, log.Message);
        Assert.Equal(raw, OpenHandsAgentRunner.UnwrapRuntimeLogs(raw));
    }

    private sealed class Handler(IEnumerable<HttpStatusCode> statuses, Task? gate = null) : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> remaining = new(statuses);
        public List<string> Payloads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Payloads.Add(await request.Content!.ReadAsStringAsync(token));
            if (gate is not null) await gate.WaitAsync(token);
            return new HttpResponseMessage(remaining.Count > 0 ? remaining.Dequeue() : HttpStatusCode.OK);
        }
    }
    private sealed class Runtime(string logs) : IJobRuntime
    {
        public Task<RuntimeJobStartResult> StartJobAsync(RuntimeJobSpec spec, CancellationToken token) => throw new NotSupportedException();
        public Task<RuntimeJobResult?> TryGetJobResultAsync(string id, CancellationToken token) => Task.FromResult<RuntimeJobResult?>(new(true, id, logs, null));
        public Task<string> ReadJobLogsAsync(string id, CancellationToken token) => Task.FromResult(logs);
    }
}
