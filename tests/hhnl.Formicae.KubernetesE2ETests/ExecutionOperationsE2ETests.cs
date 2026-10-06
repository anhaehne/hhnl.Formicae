using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.Kubernetes;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;

namespace hhnl.Formicae.KubernetesE2ETests;

public sealed partial class KubernetesWorkflowE2ETests
{
    [Fact]
    public async Task Upgraded_history_has_durable_log_cursors_and_native_stream_replay()
    {
        await WithDiagnosticsAsync(async () =>
        {
            const string id = "74000000-0000-0000-0000-000000000001";
            long cursor;
            using (var forward = await fixture.StartApiPortForwardAsync())
            using (var http = new HttpClient { BaseAddress = forward.BaseAddress })
            {
                var page = await http.GetFromJsonAsync<JsonElement>($"/api/workflows/{id}/logs/page?limit=1");
                var entry = Assert.Single(page.GetProperty("items").EnumerateArray());
                cursor = entry.GetProperty("sequence").GetInt64();
                Assert.True(cursor > 0);
                Assert.Contains("preserved retry log", entry.GetProperty("message").GetString());
                using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/workflows/{id}/logs/stream");
                request.Headers.Add("Last-Event-ID", "0");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
                var eventLines = new List<string>();
                while (await reader.ReadLineAsync(timeout.Token) is { } line)
                {
                    if (line.Length == 0 && eventLines.Count > 0) break;
                    eventLines.Add(line);
                }
                Assert.Contains(eventLines, line => line.StartsWith("id:", StringComparison.Ordinal) && line[3..].Trim() == cursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.Contains(eventLines, line => line.StartsWith("event:", StringComparison.Ordinal) && line[6..].Trim() == "log");
                Assert.Contains(eventLines, line => line.Contains("preserved retry log", StringComparison.Ordinal));
            }
            await fixture.RestartApiAsync();
            using var restarted = await fixture.StartApiPortForwardAsync();
            using var persisted = new HttpClient { BaseAddress = restarted.BaseAddress };
            var replay = await persisted.GetFromJsonAsync<JsonElement>($"/api/workflows/{id}/logs/page?after={cursor - 1}");
            Assert.Equal(cursor, Assert.Single(replay.GetProperty("items").EnumerateArray()).GetProperty("sequence").GetInt64());
            var after = await persisted.GetFromJsonAsync<JsonElement>($"/api/workflows/{id}/logs/page?after={cursor}");
            Assert.Empty(after.GetProperty("items").EnumerateArray());
            var evidence = await persisted.GetFromJsonAsync<JsonElement>($"/api/workflows/{id}/evidence");
            Assert.Equal(id, evidence.GetProperty("execution").GetProperty("workflow").GetProperty("workflowId").GetString());
            Assert.Contains("preserved retry log", evidence.ToString());
        });
    }

    [Fact]
    public async Task Runtime_logs_are_available_live_and_retained_until_completion_acknowledgment()
    {
        await WithDiagnosticsAsync(async () =>
        {
            const string ns = "formicae";
            var name = $"log-evidence-{Guid.NewGuid():N}";
            using var api = new FixtureJobApi(fixture.KubeconfigPath);
            var runtime = new KubernetesJobRunner(api, Options.Create(new KubernetesJobOptions
                { Namespace = ns, TimeoutSeconds = 120, DeleteFinishedJobs = true }), []);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                await runtime.StartJobAsync(new RuntimeJobSpec(name, "localhost/hhnl-formicae-api:e2e",
                    new Dictionary<string, string>(), ["/bin/sh", "-c", "echo live-log-evidence; sleep 10; echo completed-log-evidence"],
                    AuthMethod: RuntimeJobAuthMethods.None), timeout.Token);
                await WaitForLogAsync(runtime, name, "live-log-evidence", timeout.Token);
                Assert.Null(await runtime.TryGetJobResultAsync(name, timeout.Token));
                RuntimeJobResult? result;
                while ((result = await runtime.TryGetJobResultAsync(name, timeout.Token)) is null)
                    await Task.Delay(250, timeout.Token);
                Assert.True(result.Succeeded);
                Assert.Contains("live-log-evidence", result.Logs);
                Assert.Contains("completed-log-evidence", result.Logs);
                Assert.NotNull(await api.ReadJobStatusAsync(name, ns, timeout.Token));
                Assert.Contains("completed-log-evidence", await runtime.ReadJobLogsAsync(name, timeout.Token));
                Assert.NotNull(await runtime.TryGetJobResultAsync(name, timeout.Token));
                await runtime.AcknowledgeCompletionAsync(name, timeout.Token);
                await runtime.AcknowledgeCompletionAsync(name, timeout.Token);
                await WaitForJobRemovalAsync(api, name, ns, timeout.Token);
            }
            finally
            {
                await DeleteTestJobAsync(api, name, ns);
            }
        });
    }

    [Fact]
    public async Task Cancel_terminates_an_active_job_even_when_finished_job_retention_is_enabled()
    {
        await WithDiagnosticsAsync(async () =>
        {
            const string ns = "formicae";
            var name = $"cancel-execution-{Guid.NewGuid():N}";
            using var api = new FixtureJobApi(fixture.KubeconfigPath);
            var runtime = new KubernetesJobRunner(api, Options.Create(new KubernetesJobOptions
                { Namespace = ns, TimeoutSeconds = 120, DeleteFinishedJobs = false }), []);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                await runtime.StartJobAsync(new RuntimeJobSpec(name, "localhost/hhnl-formicae-api:e2e",
                    new Dictionary<string, string>(), ["/bin/sh", "-c", "echo cancellation-evidence; sleep 60"],
                    AuthMethod: RuntimeJobAuthMethods.None), timeout.Token);
                var evidence = await WaitForLogAsync(runtime, name, "cancellation-evidence", timeout.Token);
                Assert.Null(await runtime.TryGetJobResultAsync(name, timeout.Token));
                while (true)
                {
                    try { await runtime.CancelJobAsync(name, timeout.Token); break; }
                    catch (InvalidOperationException exception) when (exception.Message.Contains("termination is still in progress", StringComparison.Ordinal))
                    { await Task.Delay(250, timeout.Token); }
                }
                await runtime.CancelJobAsync(name, timeout.Token);
                await WaitForJobRemovalAsync(api, name, ns, timeout.Token);
                Assert.Contains("cancellation-evidence", evidence);
            }
            finally
            {
                await DeleteTestJobAsync(api, name, ns);
            }
        });
    }

    private static async Task<string> WaitForLogAsync(KubernetesJobRunner runtime, string name, string marker, CancellationToken token)
    {
        while (true)
        {
            var logs = await runtime.ReadJobLogsAsync(name, token);
            if (logs.Contains(marker, StringComparison.Ordinal)) return logs;
            await Task.Delay(250, token);
        }
    }

    private static async Task WaitForJobRemovalAsync(FixtureJobApi api, string name, string ns, CancellationToken token)
    {
        while (true)
        {
            try { await api.ReadJobStatusAsync(name, ns, token); }
            catch (k8s.Autorest.HttpOperationException exception) when (exception.Response?.StatusCode == System.Net.HttpStatusCode.NotFound) { break; }
            await Task.Delay(250, token);
        }
        while ((await api.ListPodsAsync(ns, $"job-name={name}", token)).Count > 0)
            await Task.Delay(250, token);
    }

    private static async Task DeleteTestJobAsync(FixtureJobApi api, string name, string ns)
    {
        try { await api.DeleteJobAsync(name, ns, CancellationToken.None); }
        catch (k8s.Autorest.HttpOperationException exception) when (exception.Response?.StatusCode == System.Net.HttpStatusCode.NotFound) { }
    }
}
