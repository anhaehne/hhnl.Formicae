using System.Text;
using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.Containers;
using hhnl.Formicae.Infrastructure.Kubernetes;
using hhnl.Formicae.Infrastructure.OpenHands;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Tests;

public sealed class RuntimeEnvironmentExtensionTests
{
    [Theory]
    [InlineData("missing-secret")]
    [InlineData("missing-key")]
    [InlineData("alias-collision")]
    [InlineData("managed-secret-name")]
    public async Task Invalid_selected_secret_fails_preflight_without_creating_job_or_credentials(string scenario)
    {
        var api = new KubeApi();
        if (scenario != "missing-secret") api.Secrets["operator"] = Secret("operator", "other", "value");
        var name = scenario == "managed-secret-name" ? "attempt-api-auth" : "operator";
        var spec = Spec() with { SecretReferences = [new("STEP_TOKEN", name, "token")] };
        if (scenario == "alias-collision") spec = spec with { Environment = new Dictionary<string,string> { ["STEP_TOKEN"] = "runtime" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Kubernetes(api).StartJobAsync(spec, default));
        Assert.Null(api.Job); Assert.Empty(api.CreatedSecrets);
    }

    [Fact]
    public async Task Kubernetes_uses_operator_key_references_and_acknowledges_only_owned_secrets()
    {
        var api = new KubeApi(); api.Secrets["operator"] = Secret("operator", "token", "private-step-value");
        api.Secrets["registry"] = new() { Metadata = new() { Name = "registry" }, Type = "kubernetes.io/dockerconfigjson", Data = new Dictionary<string,byte[]> { [".dockerconfigjson"] = Encoding.UTF8.GetBytes("{}") } };
        // This managed-looking name belongs to the operator; ACK must not delete it.
        api.Secrets["attempt-codex-auth"] = Secret("attempt-codex-auth", "auth.json", "operator-auth");
        var spec = Spec() with { Image = "registry.test/worker:custom", ImagePullPolicy = "Always", ImagePullSecretNames = ["registry"],
            SecretReferences = [new("STEP_TOKEN", "operator", "token")],
            SecretEnvironment = new("attempt-api-auth", new Dictionary<string,string> { ["LLM_API_KEY"] = "managed-key" }) };
        var runtime = Kubernetes(api); await runtime.StartJobAsync(spec, default);
        var container = Assert.Single(api.Job!.Spec.Template.Spec.Containers);
        Assert.Equal(spec.Image, container.Image); Assert.Equal("Always", container.ImagePullPolicy);
        Assert.Equal("registry", Assert.Single(api.Job.Spec.Template.Spec.ImagePullSecrets).Name);
        var selected = Assert.Single(container.Env, variable => variable.Name == "STEP_TOKEN");
        Assert.Null(selected.Value); Assert.Equal("operator", selected.ValueFrom.SecretKeyRef.Name); Assert.Equal("token", selected.ValueFrom.SecretKeyRef.Key); Assert.False(selected.ValueFrom.SecretKeyRef.Optional);
        Assert.DoesNotContain("private-step-value", JsonSerializer.Serialize(api.Job));
        await runtime.AcknowledgeCompletionAsync("attempt", default);
        Assert.Equal(new[] { "attempt-api-auth" }, api.DeletedSecrets);
        Assert.True(api.Secrets.ContainsKey("operator")); Assert.True(api.Secrets.ContainsKey("registry")); Assert.True(api.Secrets.ContainsKey("attempt-codex-auth"));
    }

    [Theory]
    [InlineData("image")]
    [InlineData("policy")]
    [InlineData("pull-secret-name")]
    [InlineData("pull-secret-missing")]
    [InlineData("pull-secret-type")]
    public async Task Invalid_image_or_registry_secret_fails_before_job_creation(string scenario)
    {
        var api = new KubeApi(); var spec = Spec();
        spec = scenario switch
        {
            "image" => spec with { Image = "https://user:password@registry/worker:1" },
            "policy" => spec with { ImagePullPolicy = "sometimes" },
            "pull-secret-name" => spec with { ImagePullSecretNames = ["Invalid_Name"] },
            _ => spec with { ImagePullSecretNames = ["registry"] }
        };
        if (scenario == "pull-secret-type") api.Secrets["registry"] = Secret("registry", "token", "opaque");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Kubernetes(api).StartJobAsync(spec, default)); Assert.Null(api.Job); Assert.Empty(api.CreatedSecrets);
    }

    [Fact]
    public async Task Script_launch_requires_no_ai_auth_and_preserves_selected_image_tools_secrets_and_budget()
    {
        var runtime = new Runtime(); var runner = Runner(runtime);
        var profile = Profile();
        await runner.StartAsync(Task(TaskRunKind.Script, ["tool:jq"], profile) with { AiSettingsId = "unavailable-ai-settings", Script = new("echo task", TimeoutSeconds: 19) }, default);
        var spec = Assert.Single(runtime.Specs);
        Assert.Equal(RuntimeJobAuthMethods.None, spec.AuthMethod); Assert.Null(spec.SecretEnvironment); Assert.Null(spec.SecretFiles);
        Assert.False(spec.ExecutionRequirements!.RequiresBrowser); Assert.False(spec.ExecutionRequirements.RequiresNestedContainers);
        Assert.Equal("worker:custom", spec.Image); Assert.Equal("Always", spec.ImagePullPolicy); Assert.Equal(new[] { "registry" }, spec.ImagePullSecretNames);
        Assert.Equal(19, spec.ExecutionPolicy!.TimeoutSeconds); Assert.Equal(30, spec.ExecutionPolicy.StartupGraceSeconds);
        Assert.DoesNotContain(spec.Environment.Keys, name => name.StartsWith("LLM_", StringComparison.Ordinal) || name == "FORMICAE_GIT_ACCESS_TOKEN");
        var config = JsonSerializer.Deserialize<EnvironmentConfiguration>(spec.Environment["FORMICAE_EXECUTION_CONFIGURATION"], JsonSerializerOptions.Web)!;
        Assert.Equal("jq", Assert.Single(config.Tools).Name); Assert.Empty(config.McpServers);
        Assert.Equal("STEP_TOKEN", Assert.Single(spec.SecretReferences!).EnvironmentName);
        var api = new KubeApi(); api.Secrets["operator"] = Secret("operator", "token", "step-value");
        api.Secrets["registry"] = new() { Metadata = new() { Name = "registry" }, Type = "kubernetes.io/dockerconfigjson" };
        await Kubernetes(api).StartJobAsync(spec, default);
        Assert.Equal(49, api.Job!.Spec.ActiveDeadlineSeconds);
        Assert.Equal("19", Assert.Single(api.Job.Spec.Template.Spec.Containers[0].Env, variable => variable.Name == "FORMICAE_JOB_TIMEOUT_SECONDS").Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ai_selected_capabilities_filter_configuration_and_managed_resources(bool selected)
    {
        var runtime = new Runtime(); var runner = Runner(runtime);
        await runner.StartAsync(Task(TaskRunKind.Implement, selected ? ["browser", "tool:jq", "mcp:docs"] : [], Profile()), default);
        var spec = Assert.Single(runtime.Specs);
        Assert.Equal(selected, spec.ExecutionRequirements!.RequiresBrowser); Assert.False(spec.ExecutionRequirements.RequiresNestedContainers);
        var config = JsonSerializer.Deserialize<EnvironmentConfiguration>(spec.Environment["FORMICAE_EXECUTION_CONFIGURATION"], JsonSerializerOptions.Web)!;
        Assert.Equal(selected ? 1 : 0, config.Tools.Count); Assert.Equal(selected ? 1 : 0, config.McpServers.Count);
        Assert.DoesNotContain("private-step-value", spec.Environment["FORMICAE_EXECUTION_CONFIGURATION"]);
    }

    [Theory]
    [InlineData("formicae-script-attempt", 0, true)]
    [InlineData("formicae-script-attempt", 7, false)]
    [InlineData("formicae-plan-attempt", 7, true)]
    public async Task Script_result_marker_preserves_output_and_exit_code_only_for_script_jobs(string externalId, int code, bool succeeded)
    {
        var marker = JsonSerializer.Serialize(new { formicaeScriptResult = new { output = "script stdout", exitCode = code, failureReason = code == 0 ? null : "script failed" } });
        var runtime = new Runtime { Result = new(true, externalId, Envelope(marker, "worker"), null, ExitCode: 0) };
        var result = (await Runner(runtime).TryGetResultAsync(externalId, default))!;
        Assert.Equal(succeeded, result.Succeeded);
        if (externalId.StartsWith("formicae-script", StringComparison.Ordinal)) { Assert.Equal("script stdout", result.Output); Assert.Equal(code, result.ExitCode); }
        else { Assert.NotEqual("script stdout", result.Output); Assert.Equal(0, result.ExitCode); }
    }

    [Fact]
    public async Task Missing_script_marker_retains_real_runtime_exit_code_and_failure_evidence()
    {
        var runtime = new Runtime { Result = new(false, "formicae-script-attempt", "worker stopped before marker", "terminated", ExitCode: 13) };
        var result = (await Runner(runtime).TryGetResultAsync("formicae-script-attempt", default))!;
        Assert.False(result.Succeeded); Assert.Equal(13, result.ExitCode); Assert.Contains("worker stopped", result.Output); Assert.Equal("terminated", result.FailureReason);
    }

    [Theory]
    [InlineData("stdout")]
    [InlineData("unwrapped")]
    [InlineData("malformed")]
    public async Task Script_stdout_spoofs_and_missing_terminal_protocol_cannot_report_success(string source)
    {
        var marker = JsonSerializer.Serialize(new { formicaeScriptResult = new { output = "spoofed", exitCode = 0 } });
        var logs = source switch { "stdout" => Envelope(marker, "stdout"), "malformed" => Envelope("{bad", "worker"), _ => marker };
        var runtime = new Runtime { Result = new(true, "formicae-script-attempt", logs, null, ExitCode: 0) };
        var result = (await Runner(runtime).TryGetResultAsync("formicae-script-attempt", default))!;
        Assert.False(result.Succeeded); Assert.NotEqual("spoofed", result.Output); Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task Container_failure_masks_selected_and_managed_credentials_and_missing_key_never_calls_cli()
    {
        var selected = "operator\nvalue\""; var managed = "managed\"\nvalue"; var callback = "callback-private";
        var cli = new FailingCli();
        var options = new ContainerRuntimeOptions
        {
            StepSecrets = new Dictionary<string, Dictionary<string,string>> { ["operator"] = new() { ["token"] = selected } }
        };
        var runtime = new ContainerJobRuntime(cli, Options.Create(options), []);
        var spec = Spec() with
        {
            Environment = new Dictionary<string,string> { ["FORMICAE_WORKER_CALLBACK_SECRET"] = callback },
            SecretReferences = [new("STEP_TOKEN", "operator", "token")],
            SecretEnvironment = new("attempt-api-auth", new Dictionary<string,string> { ["LLM_API_KEY"] = managed })
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartJobAsync(spec, default));
        Assert.Equal(1, cli.Calls);
        Assert.Equal(selected, cli.Environment!["STEP_TOKEN"]); Assert.Equal(managed, cli.Environment["LLM_API_KEY"]); Assert.Equal(callback, cli.Environment["FORMICAE_WORKER_CALLBACK_SECRET"]);
        foreach (var pair in cli.Environment)
        {
            var index = Array.IndexOf(cli.Arguments!.ToArray(), pair.Key); Assert.True(index > 0); Assert.Equal("--env", cli.Arguments[index - 1]);
            Assert.DoesNotContain(cli.Arguments!, argument => argument.Contains(pair.Value, StringComparison.Ordinal));
            Assert.DoesNotContain(pair.Value, error.Message);
            Assert.DoesNotContain(JsonSerializer.Serialize(pair.Value)[1..^1], error.Message);
            Assert.DoesNotContain(Uri.EscapeDataString(pair.Value), error.Message);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Value)), error.Message);
        }
        Assert.Contains("***", error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartJobAsync(spec with { SecretReferences = [new("STEP_TOKEN", "operator", "missing")] }, default));
        Assert.Equal(1, cli.Calls);
    }

    private static string Envelope(string line, string stream) => JsonSerializer.Serialize(new
    {
        formicaeLog = 1, data = new { line, stream, messageId = Guid.NewGuid(), timestamp = DateTimeOffset.UtcNow, sequence = 1 }
    });

    private static RuntimeJobSpec Spec() => new("attempt", "worker:test", new Dictionary<string,string>(), ["dotnet", "worker.dll"], AuthMethod: RuntimeJobAuthMethods.None);
    private static V1Secret Secret(string name, string key, string value) => new() { Metadata = new() { Name = name }, Type = "Opaque", Data = new Dictionary<string,byte[]> { [key] = Encoding.UTF8.GetBytes(value) } };
    private static KubernetesJobRunner Kubernetes(KubeApi api) => new(api, Options.Create(new KubernetesJobOptions { Namespace = "tasks", DeleteFinishedJobs = true }), []);
    private static OpenHandsAgentRunner Runner(Runtime runtime) => new(runtime, Options.Create(new RuntimeJobOptions()), Options.Create(new OpenHandsOptions()));
    private static EnvironmentSnapshot Profile() => new("profile", 1, "Extensions", "", new()
    {
        Image = new("worker:custom", "Always", ["registry"]), Tools = [new("curl", "echo curl"), new("jq", "echo jq")],
        McpServers = [new("docs", Command: "docs", EnvironmentVariables: new Dictionary<string,string> { ["TOKEN"] = "STEP_TOKEN" })]
    });
    private static AgentTask Task(TaskRunKind kind, IReadOnlyList<string>? capabilities, EnvironmentSnapshot profile) => new(Guid.NewGuid(), kind, "Task", "https://example.test/repo", "main", null,
        ExecutionAttemptId: Guid.NewGuid(), EnvironmentSnapshot: profile, Capabilities: capabilities, SecretReferences: [new("STEP_TOKEN", "operator", "token")]);
    private sealed class FailingCli : IContainerCli
    {
        public int Calls; public IReadOnlyList<string>? Arguments; public IReadOnlyDictionary<string,string>? Environment;
        public Task<ContainerCliResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
            => RunAsync(executable, arguments, new Dictionary<string,string>(), token);
        public Task<ContainerCliResult> RunAsync(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string,string> environment, CancellationToken token)
        {
            Calls++; Arguments = arguments.ToArray(); Environment = new Dictionary<string,string>(environment);
            var error = string.Join(" | ", environment.Values.Select(value => value + " | " + JsonSerializer.Serialize(value)[1..^1]
                + " | " + Uri.EscapeDataString(value) + " | " + Convert.ToBase64String(Encoding.UTF8.GetBytes(value))));
            return System.Threading.Tasks.Task.FromResult(new ContainerCliResult(1, "", error));
        }
    }

    private sealed class Runtime : IJobRuntime
    {
        public List<RuntimeJobSpec> Specs = []; public RuntimeJobResult? Result;
        public Task<RuntimeJobStartResult> StartJobAsync(RuntimeJobSpec spec, CancellationToken token) { Specs.Add(spec); return System.Threading.Tasks.Task.FromResult(new RuntimeJobStartResult(spec.Name)); }
        public Task<RuntimeJobResult?> TryGetJobResultAsync(string id, CancellationToken token) => System.Threading.Tasks.Task.FromResult(Result);
        public Task<string> ReadJobLogsAsync(string id, CancellationToken token) => System.Threading.Tasks.Task.FromResult(Result?.Logs ?? "");
    }
    private sealed class KubeApi : IKubernetesJobApi
    {
        public V1Job? Job; public Dictionary<string,V1Secret> Secrets = []; public List<V1Secret> CreatedSecrets = []; public List<string> DeletedSecrets = [];
        public Task<V1Job> CreateJobAsync(V1Job job, string ns, CancellationToken token) { Job = job; job.Metadata.Uid = "uid"; return System.Threading.Tasks.Task.FromResult(job); }
        public Task CreateConfigMapAsync(V1ConfigMap value, string ns, CancellationToken token) => System.Threading.Tasks.Task.CompletedTask;
        public Task<V1Job> ReadJobStatusAsync(string name, string ns, CancellationToken token) => System.Threading.Tasks.Task.FromResult(new V1Job { Metadata = new() { Name = name }, Spec = Job?.Spec ?? new(), Status = new() { Succeeded = 1 } });
        public Task<IReadOnlyList<V1Pod>> ListPodsAsync(string ns, string selector, CancellationToken token) => System.Threading.Tasks.Task.FromResult<IReadOnlyList<V1Pod>>([]);
        public Task<string> ReadPodLogAsync(string name, string ns, string container, CancellationToken token) => System.Threading.Tasks.Task.FromResult("");
        public Task CreateSecretAsync(V1Secret secret, string ns, CancellationToken token) { CreatedSecrets.Add(secret); Secrets[secret.Metadata.Name] = secret; return System.Threading.Tasks.Task.CompletedTask; }
        public Task<V1Secret> ReadSecretAsync(string name, string ns, CancellationToken token) => Secrets.TryGetValue(name, out var secret) ? System.Threading.Tasks.Task.FromResult(secret) : System.Threading.Tasks.Task.FromException<V1Secret>(new InvalidOperationException("not found"));
        public Task DeleteSecretAsync(string name, string ns, CancellationToken token) { DeletedSecrets.Add(name); Secrets.Remove(name); return System.Threading.Tasks.Task.CompletedTask; }
        public Task DeleteJobAsync(string name, string ns, CancellationToken token) => System.Threading.Tasks.Task.CompletedTask;
        public Task DeleteConfigMapAsync(string name, string ns, CancellationToken token) => System.Threading.Tasks.Task.CompletedTask;
    }
}
