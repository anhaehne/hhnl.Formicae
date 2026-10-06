using System.Net;
using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.Kubernetes;
using hhnl.Formicae.Infrastructure.OpenHands;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.KubernetesE2ETests;

public sealed partial class KubernetesWorkflowE2ETests
{
    [Fact]
    public Task Environment_worker_installs_tool_runs_script_and_redacts_only_selected_secret()
        => WithExtensionWorkerAsync(async context =>
        {
            var secretName = "extension-secret-" + Guid.NewGuid().ToString("N");
            var secretValue = "selected-" + Guid.NewGuid().ToString("N");
            await context.CreateSecretAsync(secretName, new() { ["selected"] = secretValue, ["unselected"] = "unused-" + Guid.NewGuid().ToString("N") });
            var configuration = context.Configuration with
            {
                Tools = [new("greeting", "printf '#!/bin/sh\\nprintf tool-ready\\n' > /usr/local/bin/formicae-e2e-tool\nchmod +x /usr/local/bin/formicae-e2e-tool")]
            };
            var task = context.ScriptTask("""
                set -eu
                test -z "${unselected+x}"
                test -z "${LLM_API_KEY+x}"
                test -z "${OPENAI_API_KEY+x}"
                formicae-e2e-tool
                printf '\nscript-secret=%s\n' "$STEP_TOKEN"
                printf 'stderr-secret=%s\n' "$STEP_TOKEN" >&2
                """, configuration, secrets: [new("STEP_TOKEN", secretName, "selected")]);
            // A Script must bypass AI-settings lookup entirely.
            task = task with { AiSettingsId = "configuration-that-does-not-exist" };
            var started = await context.StartAsync(task);
            Assert.Null(started.AiSettingsId);
            var job = await context.Api.ReadJobStatusAsync(started.ExternalId, ExtensionNamespace, context.Token);
            var container = Assert.Single(job.Spec.Template.Spec.Containers);
            Assert.Equal(fixture.WorkerImage, container.Image);
            Assert.Equal("Never", container.ImagePullPolicy);
            Assert.Equal(new[] { "dotnet", "hhnl.Formicae.Worker.dll" }, container.Command);
            Assert.True(container.EnvFrom is null or { Count: 0 });
            var reference = Assert.Single(container.Env, item => item.ValueFrom?.SecretKeyRef is not null).ValueFrom.SecretKeyRef;
            Assert.Equal(secretName, reference.Name);
            Assert.Equal("selected", reference.Key);
            Assert.False(reference.Optional);
            AssertSecretAbsent(JsonSerializer.Serialize(job), secretValue);

            var result = await context.WaitAsync(started.ExternalId);
            var logs = await context.Runtime.ReadJobLogsAsync(started.ExternalId, context.Token);
            AssertSecretAbsent(result.Output + result.FailureReason + logs, secretValue);
            Assert.True(result.Succeeded, result.FailureReason);
            Assert.Equal(0, result.ExitCode);
            await context.AssertWorkerExitAsync(started.ExternalId, 0);
            Assert.Contains("tool-ready", result.Output);
            Assert.Contains("script-secret=***", result.Output);
            Assert.Contains("stderr-secret=***", logs);
            Assert.DoesNotContain("stderr-secret", result.Output);

            await context.Runtime.AcknowledgeCompletionAsync(started.ExternalId, context.Token);
            await context.WaitForRemovalAsync(started.ExternalId);
            var retainedSecret = await context.Api.ReadSecretAsync(secretName, ExtensionNamespace, context.Token);
            Assert.Equal(2, retainedSecret.Data.Count);
            AssertSecretAbsent(JsonSerializer.Serialize(retainedSecret.Metadata), secretValue);
        });

    [Fact]
    public Task Empty_capabilities_skip_installs_and_missing_secret_references_fail_before_job_creation()
        => WithExtensionWorkerAsync(async context =>
        {
            var configuration = context.Configuration with
            {
                Tools = [new("forbidden-install", "touch /workspace/forbidden-install\nexit 99")]
            };
            var task = context.ScriptTask("test ! -e /workspace/forbidden-install && printf capabilities-empty", configuration, capabilities: []);
            var started = await context.StartAsync(task);
            var result = await context.WaitAsync(started.ExternalId);
            Assert.True(result.Succeeded, result.FailureReason);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("capabilities-empty", result.Output.Trim());
            Assert.DoesNotContain("Installing environment tool", await context.Runtime.ReadJobLogsAsync(started.ExternalId, context.Token));

            var existingName = "extension-existing-" + Guid.NewGuid().ToString("N");
            await context.CreateSecretAsync(existingName, new() { ["present"] = "unused-fixture-value" });
            foreach (var reference in new[]
            {
                new WorkflowSecretReference("STEP_TOKEN", "extension-missing-" + Guid.NewGuid().ToString("N"), "present"),
                new WorkflowSecretReference("STEP_TOKEN", existingName, "absent")
            })
            {
                var missing = context.ScriptTask("exit 0", secrets: [reference]);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.StartAsync(missing));
                Assert.Contains(reference.SecretName, error.Message);
                var externalId = context.Runner.ResolveExternalId(missing.WorkflowId, missing.Kind, missing.ExecutionAttemptId)!;
                await context.AssertMissingAsync(externalId);
            }
        });

    [Fact]
    public Task Native_worker_records_script_exit_and_deadline_and_stops_after_install_failure()
        => WithExtensionWorkerAsync(async context =>
        {
            var failingScript = await context.StartAsync(context.ScriptTask("printf before-failure\nexit 17"));
            var failed = await context.WaitAsync(failingScript.ExternalId);
            Assert.False(failed.Succeeded);
            Assert.Equal(17, failed.ExitCode);
            await context.AssertWorkerExitAsync(failingScript.ExternalId, 17);
            Assert.Contains("before-failure", failed.Output);
            Assert.Contains("17", failed.FailureReason!);

            var deadlineTask = context.ScriptTask("printf 'before-deadline\\n'\nsleep 20") with
            {
                TimeoutSeconds = 3,
                Script = new("printf 'before-deadline\\n'\nsleep 20", TimeoutSeconds: 3)
            };
            var deadlineStart = await context.StartAsync(deadlineTask);
            var expired = await context.WaitAsync(deadlineStart.ExternalId);
            Assert.False(expired.Succeeded);
            Assert.Equal(124, expired.ExitCode);
            await context.AssertWorkerExitAsync(deadlineStart.ExternalId, 124);
            Assert.Contains("before-deadline", expired.Output);
            Assert.Contains("deadline", expired.FailureReason!, StringComparison.OrdinalIgnoreCase);

            foreach (var (install, exitCode) in new[]
            {
                (new EnvironmentToolInstall("broken", "printf install-failed >&2\nexit 23"), 23),
                (new EnvironmentToolInstall("slow", "printf install-started\nsleep 20", TimeoutSeconds: 1), 124)
            })
            {
                // The install deadline is shorter than the pod deadline, making the worker's
                // 124 result observable without racing the native Job deadline controller.
                var configuration = context.Configuration with { Tools = [install] };
                var started = await context.StartAsync(context.ScriptTask("printf script-must-not-start", configuration));
                var result = await context.WaitAsync(started.ExternalId);
                Assert.False(result.Succeeded);
                Assert.Equal(exitCode, result.ExitCode);
                await context.AssertWorkerExitAsync(started.ExternalId, exitCode);
                Assert.Contains(install.Name, result.FailureReason!);
                Assert.DoesNotContain("script-must-not-start", result.Output);
                Assert.DoesNotContain("script-must-not-start", await context.Runtime.ReadJobLogsAsync(started.ExternalId, context.Token));
            }
        });

    [Fact]
    public Task Native_worker_materializes_only_enabled_mcp_servers_in_codex_and_openhands_formats()
        => WithExtensionWorkerAsync(async context =>
        {
            var secretName = "extension-mcp-" + Guid.NewGuid().ToString("N");
            var secretValue = "mcp-selected-😀-" + Guid.NewGuid().ToString("N");
            await context.CreateSecretAsync(secretName, new() { ["token"] = secretValue });
            // Replace only the CLI inside this ephemeral worker. The real worker performs
            // bootstrap, secret injection and native config generation; no model is contacted.
            const string probe = """
                set -eu
                cat > /workspace/mcp-probe.py <<'PROBE'
                import json, os, pathlib, stat, sys, tomllib
                expected = {'local-check', 'remote-check'}
                if sys.argv[1] == 'codex':
                    config_path = pathlib.Path(os.environ['CODEX_HOME']) / 'config.toml'
                    config = tomllib.loads(config_path.read_text())['mcp_servers']
                    assert set(config) == expected, 'Codex server selection mismatch'
                    assert config['local-check']['args'] == ['-c', 'exit 0', 'unicode-😀'], 'Codex Unicode argument mismatch'
                    assert config['local-check']['env']['SERVER_TOKEN'] == os.environ['STEP_TOKEN'], 'Codex alias mapping failed'
                    assert config['remote-check']['bearer_token_env_var'] == 'STEP_TOKEN', 'Codex bearer alias missing'
                    assert config['remote-check']['env_http_headers']['X-Api-Key'] == 'STEP_TOKEN', 'Codex header alias missing'
                    result = {'type':'item.completed','item':{'type':'agent_message','text':'native-mcp-configuration-verified'}}
                else:
                    config_path = pathlib.Path(os.environ['OPENHANDS_PERSISTENCE_DIR']) / 'mcp.json'
                    config = json.loads(config_path.read_text())['mcpServers']
                    assert set(config) == expected, 'OpenHands server selection mismatch'
                    assert config['local-check']['args'] == ['-c', 'exit 0', 'unicode-😀'], 'OpenHands Unicode argument mismatch'
                    assert config['local-check']['env']['SERVER_TOKEN'] == os.environ['STEP_TOKEN'], 'OpenHands alias mapping failed'
                    assert config['remote-check']['transport'] == 'http', 'OpenHands transport mismatch'
                    assert config['remote-check']['headers']['Authorization'] == 'Bearer ' + os.environ['STEP_TOKEN'], 'OpenHands bearer mapping failed'
                    assert config['remote-check']['headers']['X-Api-Key'] == os.environ['STEP_TOKEN'], 'OpenHands header mapping failed'
                    result = {'kind':'ActionEvent','source':'agent','action':{'kind':'FinishAction','message':'native-mcp-configuration-verified'}}
                assert stat.S_IMODE(config_path.stat().st_mode) == 0o600, 'Native config permissions mismatch'
                print(json.dumps(result))
                PROBE
                printf '#!/bin/sh\nexec python3 /workspace/mcp-probe.py codex\n' > /usr/local/bin/npx
                printf '#!/bin/sh\nexec python3 /workspace/mcp-probe.py openhands\n' > /root/.local/bin/openhands
                chmod 755 /usr/local/bin/npx /root/.local/bin/openhands
                """;
            var configuration = context.Configuration with
            {
                Tools = [new("mcp-probe", probe)],
                McpServers =
                [
                    new("local-check", Command: "sh", Arguments: ["-c", "exit 0", "unicode-😀"], EnvironmentVariables: new Dictionary<string, string> { ["SERVER_TOKEN"] = "STEP_TOKEN" }),
                    new("remote-check", Transport: "http", Url: "https://example.invalid/mcp", BearerTokenEnvironmentVariable: "STEP_TOKEN",
                        HeaderEnvironmentVariables: new Dictionary<string, string> { ["X-Api-Key"] = "STEP_TOKEN" }),
                    // Its absent secret must not be resolved because this server is not enabled.
                    new("excluded", Command: "false", EnvironmentVariables: new Dictionary<string, string> { ["UNAVAILABLE"] = "NOT_SELECTED" })
                ]
            };
            var task = new AgentTask(Guid.NewGuid(), TaskRunKind.Custom, "Verify native MCP preparation.", "https://example.invalid/repo", "main", null,
                ExecutionAttemptId: Guid.NewGuid(), TimeoutSeconds: 60, EnvironmentSnapshot: context.Snapshot(configuration),
                Capabilities: ["tool:mcp-probe", "mcp:local-check", "mcp:remote-check"], SecretReferences: [new("STEP_TOKEN", secretName, "token")]);
            foreach (var auth in new[] { OpenHandsAuthMethods.CodexSubscription, OpenHandsAuthMethods.ApiKey })
            {
                var started = await context.StartAsync(task with { WorkflowId = Guid.NewGuid(), ExecutionAttemptId = Guid.NewGuid() }, auth);
                var result = await context.WaitAsync(started.ExternalId);
                var logs = await context.Runtime.ReadJobLogsAsync(started.ExternalId, context.Token);
                AssertSecretAbsent(result.Output + result.FailureReason + logs, secretValue);
                Assert.True(result.Succeeded, result.FailureReason);
                Assert.Equal("native-mcp-configuration-verified", result.Output);
            }
        }, OpenHandsAuthMethods.CodexSubscription);

    private const string ExtensionNamespace = "formicae";

    private Task WithExtensionWorkerAsync(Func<ExtensionWorkerContext, Task> action, string authMethod = OpenHandsAuthMethods.ApiKey)
        => WithDiagnosticsAsync(async () =>
        {
            await using var context = new ExtensionWorkerContext(fixture.KubeconfigPath, fixture.WorkerImage, authMethod);
            await action(context);
        });

    private static void AssertSecretAbsent(string text, string secret)
        => Assert.False(text.Contains(secret, StringComparison.Ordinal), "A selected secret value was exposed in operator-visible evidence.");

    private sealed class ExtensionWorkerContext : IAsyncDisposable
    {
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromMinutes(5));
        private readonly List<string> jobs = [];
        private readonly List<string> secrets = [];
        public ExtensionJobApi Api { get; }
        public KubernetesJobRunner Runtime { get; }
        public OpenHandsAgentRunner Runner { get; }
        public EnvironmentConfiguration Configuration { get; }
        public CancellationToken Token => deadline.Token;

        public ExtensionWorkerContext(string kubeconfigPath, string workerImage, string authMethod)
        {
            Api = new(kubeconfigPath);
            Runtime = new(Api, Options.Create(new KubernetesJobOptions { Namespace = ExtensionNamespace, TimeoutSeconds = 120, DeleteFinishedJobs = true }), []);
            Runner = new(Runtime, Options.Create(new RuntimeJobOptions { Image = "example.invalid/unselected-default:must-not-run" }),
                Options.Create(new OpenHandsOptions { AuthMethod = authMethod }));
            Configuration = new() { Image = new(workerImage, "Never") };
        }

        public EnvironmentSnapshot Snapshot(EnvironmentConfiguration configuration) => new("worker-e2e", 4, "Pinned worker", "Native worker E2E profile", configuration);
        public AgentTask ScriptTask(string script, EnvironmentConfiguration? configuration = null, IReadOnlyList<string>? capabilities = null,
            IReadOnlyList<WorkflowSecretReference>? secrets = null)
            => new(Guid.NewGuid(), TaskRunKind.Script, "", "https://example.invalid/repo", "main", null,
                ExecutionAttemptId: Guid.NewGuid(), TimeoutSeconds: 60, EnvironmentSnapshot: Snapshot(configuration ?? Configuration),
                Capabilities: capabilities, SecretReferences: secrets, Script: new(script, TimeoutSeconds: 60));

        public async Task CreateSecretAsync(string name, Dictionary<string, string> values)
        {
            await Api.CreateSecretAsync(new V1Secret { Metadata = new() { Name = name }, Type = "Opaque", StringData = values }, ExtensionNamespace, Token);
            secrets.Add(name);
        }

        public async Task<AgentRunStartResult> StartAsync(AgentTask task, string? authMethod = null)
        {
            var runner = authMethod is null ? Runner : new OpenHandsAgentRunner(Runtime,
                Options.Create(new RuntimeJobOptions { Image = "example.invalid/unselected-default:must-not-run" }),
                Options.Create(new OpenHandsOptions { AuthMethod = authMethod }));
            var started = await runner.StartAsync(task, Token);
            jobs.Add(started.ExternalId);
            return started;
        }

        public async Task<AgentRunResult> WaitAsync(string externalId)
        {
            while (true)
            {
                if (await Runner.TryGetResultAsync(externalId, Token) is { } result) return result;
                await Task.Delay(500, Token);
            }
        }

        public async Task AssertMissingAsync(string externalId)
        {
            var error = await Assert.ThrowsAsync<k8s.Autorest.HttpOperationException>(() => Api.ReadJobStatusAsync(externalId, ExtensionNamespace, Token));
            Assert.Equal(HttpStatusCode.NotFound, error.Response.StatusCode);
        }

        public async Task AssertWorkerExitAsync(string externalId, int exitCode)
        {
            var pod = Assert.Single(await Api.ListPodsAsync(ExtensionNamespace, $"job-name={externalId}", Token));
            var worker = Assert.Single(pod.Status.ContainerStatuses, status => status.Name == "worker");
            Assert.NotNull(worker.State.Terminated);
            Assert.Equal(exitCode, worker.State.Terminated.ExitCode);
        }

        public async Task WaitForRemovalAsync(string name)
        {
            while (true)
            {
                try { await Api.ReadJobStatusAsync(name, ExtensionNamespace, Token); }
                catch (k8s.Autorest.HttpOperationException error) when (error.Response?.StatusCode == HttpStatusCode.NotFound) { break; }
                await Task.Delay(250, Token);
            }
            while ((await Api.ListPodsAsync(ExtensionNamespace, $"job-name={name}", Token)).Count > 0) await Task.Delay(250, Token);
        }

        public async ValueTask DisposeAsync()
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                foreach (var name in jobs)
                    try { await Api.DeleteJobAsync(name, ExtensionNamespace, cleanup.Token); }
                    catch (k8s.Autorest.HttpOperationException error) when (error.Response?.StatusCode == HttpStatusCode.NotFound) { }
                foreach (var name in secrets)
                    try { await Api.DeleteSecretAsync(name, ExtensionNamespace, cleanup.Token); }
                    catch (k8s.Autorest.HttpOperationException error) when (error.Response?.StatusCode == HttpStatusCode.NotFound) { }
            }
            finally { Api.Dispose(); deadline.Dispose(); }
        }
    }

    private sealed class ExtensionJobApi(string kubeconfigPath) : IKubernetesJobApi, IDisposable
    {
        private readonly Kubernetes client = new(KubernetesClientConfiguration.BuildConfigFromConfigFile(kubeconfigPath));
        public Task<V1Job> CreateJobAsync(V1Job job, string ns, CancellationToken token) => client.BatchV1.CreateNamespacedJobAsync(job, ns, cancellationToken: token);
        public Task<V1Job> ReadJobStatusAsync(string name, string ns, CancellationToken token) => client.BatchV1.ReadNamespacedJobStatusAsync(name, ns, cancellationToken: token);
        public Task<V1Secret> ReadSecretAsync(string name, string ns, CancellationToken token) => client.CoreV1.ReadNamespacedSecretAsync(name, ns, cancellationToken: token);
        public async Task<IReadOnlyList<V1Pod>> ListPodsAsync(string ns, string selector, CancellationToken token)
            => (await client.CoreV1.ListNamespacedPodAsync(ns, labelSelector: selector, cancellationToken: token)).Items.ToArray();
        public async Task<string> ReadPodLogAsync(string name, string ns, string container, CancellationToken token)
        {
            await using var stream = await client.CoreV1.ReadNamespacedPodLogAsync(name, ns, container: container, cancellationToken: token);
            using var reader = new StreamReader(stream); return await reader.ReadToEndAsync(token);
        }
        public Task CreateConfigMapAsync(V1ConfigMap map, string ns, CancellationToken token) => client.CoreV1.CreateNamespacedConfigMapAsync(map, ns, cancellationToken: token);
        public Task CreateSecretAsync(V1Secret secret, string ns, CancellationToken token) => client.CoreV1.CreateNamespacedSecretAsync(secret, ns, cancellationToken: token);
        public Task DeleteSecretAsync(string name, string ns, CancellationToken token) => client.CoreV1.DeleteNamespacedSecretAsync(name, ns, cancellationToken: token);
        public Task DeleteConfigMapAsync(string name, string ns, CancellationToken token) => client.CoreV1.DeleteNamespacedConfigMapAsync(name, ns, cancellationToken: token);
        public Task DeleteJobAsync(string name, string ns, CancellationToken token)
            => client.BatchV1.DeleteNamespacedJobAsync(name, ns, new V1DeleteOptions { PropagationPolicy = "Background" }, cancellationToken: token);
        public void Dispose() => client.Dispose();
    }
}
