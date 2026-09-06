using System.Net;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.OpenHands;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Tests;

public sealed class AgentExecutionIdentityTests
{
    private static AgentTask TaskDefinition() => new(Guid.NewGuid(), TaskRunKind.Plan, "plan", "https://example.com/repo", "main", null,
        ExecutionAttemptId: Guid.NewGuid());

    private static OpenHandsAgentRunner Runner(Runtime runtime) => new(runtime,
        Options.Create(new RuntimeJobOptions()), Options.Create(new OpenHandsOptions()));

    [Fact]
    public async Task Repeated_durable_attempt_uses_same_job_identity_even_if_prompt_changes()
    {
        var runtime = new Runtime();
        var runner = Runner(runtime);
        var task = TaskDefinition();
        var first = await runner.StartAsync(task, default);
        var second = await runner.StartAsync(task with { Prompt = "regenerated context" }, default);
        Assert.Equal(first.ExternalId, second.ExternalId);
        Assert.Equal(runtime.Specs[0].Name, runtime.Specs[1].Name);
        Assert.All(runtime.Specs, spec => Assert.True(spec.ReuseExisting));
        Assert.Matches("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$", first.ExternalId);
        Assert.InRange(first.ExternalId.Length, 1, 63);
    }

    [Fact]
    public async Task Different_attempt_workflow_and_task_kind_have_distinct_names()
    {
        var runtime = new Runtime();
        var runner = Runner(runtime);
        var task = TaskDefinition();
        foreach (var variant in new[] { task, task with { ExecutionAttemptId = Guid.NewGuid() },
            task with { WorkflowId = Guid.NewGuid() }, task with { Kind = TaskRunKind.AddressComments } })
            await runner.StartAsync(variant, default);
        Assert.Equal(4, runtime.Specs.Select(spec => spec.Name).Distinct().Count());
        Assert.All(runtime.Specs, spec => Assert.InRange(spec.Name.Length, 1, 63));
    }

    [Fact]
    public async Task Legacy_tasks_without_durable_attempt_keep_distinct_launch_names()
    {
        var runtime = new Runtime();
        var runner = Runner(runtime);
        var task = TaskDefinition() with { ExecutionAttemptId = null };
        Assert.NotEqual((await runner.StartAsync(task, default)).ExternalId, (await runner.StartAsync(task, default)).ExternalId);
        Assert.All(runtime.Specs, spec => Assert.False(spec.ReuseExisting));
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Kubernetes_transient_response_preserves_launch_uncertainty(int status)
    {
        var failure = new k8s.Autorest.HttpOperationException("request failed")
        {
            Response = new k8s.Autorest.HttpResponseMessageWrapper(new HttpResponseMessage((HttpStatusCode)status), "")
        };
        var runner = Runner(new Runtime { Failure = failure });
        var error = await Assert.ThrowsAsync<AgentLaunchUncertainException>(() => runner.StartAsync(TaskDefinition(), default));
        Assert.Same(failure, error.InnerException);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task Kubernetes_permanent_errors_remain_failures(int status)
    {
        var failure = new k8s.Autorest.HttpOperationException("invalid configuration")
        {
            Response = new k8s.Autorest.HttpResponseMessageWrapper(new HttpResponseMessage((HttpStatusCode)status), "")
        };
        var error = await Assert.ThrowsAsync<k8s.Autorest.HttpOperationException>(() => Runner(new Runtime { Failure = failure }).StartAsync(TaskDefinition(), default));
        Assert.Same(failure, error);
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("timeout")]
    [InlineData("internal-cancellation")]
    public async Task Transport_and_internal_timeout_are_uncertain_for_durable_launches(string kind)
    {
        Exception failure = kind switch
        {
            "transport" => new HttpRequestException("connection lost"),
            "timeout" => new TimeoutException(),
            _ => new TaskCanceledException()
        };
        await Assert.ThrowsAsync<AgentLaunchUncertainException>(() => Runner(new Runtime { Failure = failure }).StartAsync(TaskDefinition(), default));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reclassified_as_launch_uncertainty()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Runner(new Runtime { Failure = new OperationCanceledException(cancellation.Token) })
            .StartAsync(TaskDefinition(), cancellation.Token));
    }

    [Fact]
    public async Task Local_filesystem_errors_are_not_reclassified_as_transport_uncertainty()
        => await Assert.ThrowsAsync<IOException>(() => Runner(new Runtime { Failure = new IOException("disk full") }).StartAsync(TaskDefinition(), default));

    private sealed class Runtime : IJobRuntime
    {
        public List<RuntimeJobSpec> Specs { get; } = [];
        public Exception? Failure { get; init; }
        public Task<RuntimeJobStartResult> StartJobAsync(RuntimeJobSpec spec, CancellationToken cancellationToken)
        {
            Specs.Add(spec);
            return Failure is null ? Task.FromResult(new RuntimeJobStartResult(spec.Name)) : Task.FromException<RuntimeJobStartResult>(Failure);
        }
        public Task<RuntimeJobResult?> TryGetJobResultAsync(string externalId, CancellationToken cancellationToken) => Task.FromResult<RuntimeJobResult?>(null);
        public Task<string> ReadJobLogsAsync(string externalId, CancellationToken cancellationToken) => Task.FromResult("");
    }
}
