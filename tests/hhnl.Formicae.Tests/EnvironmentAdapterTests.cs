using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.Containers;
using hhnl.Formicae.Infrastructure.Kubernetes;
using hhnl.Formicae.Infrastructure.OpenHands;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Tests;

public sealed class EnvironmentAdapterTests
{
    [Theory]
    [InlineData(null, null, 900, false)]
    [InlineData(40, null, 40, true)]
    [InlineData(1000, null, 900, true)]
    [InlineData(1, 3600, 1, true)]
    [InlineData(100, 43, 43, true)]
    public async Task Both_adapters_propagate_cap_and_same_effective_policy_including_prior_null_policy(int? cap, int? explicitTimeout, int effective, bool propagated)
    {
        var spec = new RuntimeJobSpec("environment-test", "worker:test", new Dictionary<string, string>(), ["worker"],
            ExecutionPolicy: explicitTimeout.HasValue ? new(explicitTimeout.Value, explicitTimeout == 43 ? 0 : 600) : null, TimeoutLimitSeconds: cap);
        var api = System.Reflection.DispatchProxy.Create<IKubernetesJobApi, CaptureKubernetes>();
        await new KubernetesJobRunner(api, Options.Create(new KubernetesJobOptions { TimeoutSeconds = 900 }), []).StartJobAsync(spec, default);
        var job = ((CaptureKubernetes)api).Job!;
        Assert.Equal(effective, job.Spec.ActiveDeadlineSeconds);
        var env = Assert.Single(job.Spec.Template.Spec.Containers).Env.ToDictionary(item => item.Name, item => item.Value);
        var cli = new CaptureContainer();
        await new ContainerJobRuntime(cli, Options.Create(new ContainerRuntimeOptions { TimeoutSeconds = 900 }), []).StartJobAsync(spec, default);
        Assert.Contains($"formicae.timeout-seconds={effective}", cli.Arguments);
        Assert.Equal(propagated, env.ContainsKey("FORMICAE_JOB_TIMEOUT_SECONDS"));
        Assert.Equal(propagated, cli.Arguments.Contains($"FORMICAE_JOB_TIMEOUT_SECONDS={effective}"));
        Assert.Equal(cap.HasValue, env.ContainsKey("FORMICAE_ENVIRONMENT_TIMEOUT_LIMIT"));
        Assert.Equal(cap.HasValue, cli.Arguments.Contains("FORMICAE_ENVIRONMENT_TIMEOUT_LIMIT=true"));
        if (propagated)
        {
            Assert.Equal(effective.ToString(), env["FORMICAE_JOB_TIMEOUT_SECONDS"]);
            var grace = explicitTimeout.HasValue && explicitTimeout != 43 ? Math.Min(600, effective - 1) : 0;
            Assert.Equal(grace.ToString(), env["FORMICAE_CHECKPOINT_GRACE_SECONDS"]);
            Assert.Contains($"FORMICAE_CHECKPOINT_GRACE_SECONDS={grace}", cli.Arguments);
        }
    }

    [Theory]
    [InlineData(TaskRunKind.Plan, null)]
    [InlineData(TaskRunKind.Implement, 3600)]
    [InlineData(TaskRunKind.Custom, 43)]
    public async Task Runner_transfers_profile_cap_without_replacing_kind_policy(TaskRunKind kind, int? expectedTaskTimeout)
    {
        var runtime = new CaptureRuntime();
        var runner = new OpenHandsAgentRunner(runtime, Options.Create(new RuntimeJobOptions()), Options.Create(new OpenHandsOptions()));
        var profile = new EnvironmentSnapshot("profile", 2, "Bounded", "", new() { Runtime = new(60) });
        await runner.StartAsync(new(Guid.NewGuid(), kind, "prompt", "https://example.test/repo", "main", null,
            TimeoutSeconds: kind == TaskRunKind.Custom ? 43 : null, EnvironmentSnapshot: profile), default);
        Assert.Equal(60, runtime.Spec!.TimeoutLimitSeconds);
        Assert.Equal(expectedTaskTimeout, runtime.Spec.ExecutionPolicy?.TimeoutSeconds);
    }

    [Fact]
    public async Task Direct_runner_rejects_unsupported_profile_configuration_before_creating_a_job()
    {
        var runtime = new CaptureRuntime();
        var runner = new OpenHandsAgentRunner(runtime, Options.Create(new RuntimeJobOptions()), Options.Create(new OpenHandsOptions()));
        var profile = new EnvironmentSnapshot("profile", 2, "Unsupported", "", new() { SchemaVersion = 99 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.StartAsync(new(Guid.NewGuid(), TaskRunKind.Plan,
            "prompt", "https://example.test/repo", "main", null, EnvironmentSnapshot: profile), default));
        Assert.Null(runtime.Spec);
    }

    public class CaptureKubernetes : System.Reflection.DispatchProxy
    {
        public V1Job? Job;
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IKubernetesJobApi.CreateJobAsync)) { Job = (V1Job)args![0]!; return Task.FromResult(Job); }
            return Task.CompletedTask;
        }
    }
    private sealed class CaptureContainer : IContainerCli
    {
        public IReadOnlyList<string> Arguments = [];
        public Task<ContainerCliResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        { Arguments = arguments.ToArray(); return Task.FromResult(new ContainerCliResult(0, "created", "")); }
    }
    private sealed class CaptureRuntime : IJobRuntime
    {
        public RuntimeJobSpec? Spec;
        public Task<RuntimeJobStartResult> StartJobAsync(RuntimeJobSpec spec, CancellationToken token)
        { Spec = spec; return Task.FromResult(new RuntimeJobStartResult(spec.Name)); }
        public Task<RuntimeJobResult?> TryGetJobResultAsync(string id, CancellationToken token) => Task.FromResult<RuntimeJobResult?>(null);
        public Task<string> ReadJobLogsAsync(string id, CancellationToken token) => Task.FromResult("");
    }
}
