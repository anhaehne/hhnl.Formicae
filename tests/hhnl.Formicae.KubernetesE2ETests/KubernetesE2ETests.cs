using System.Net.Http.Json;
using System.Text.Json;
using hhnl.Formicae.KubernetesE2ETests.Infrastructure;

namespace hhnl.Formicae.KubernetesE2ETests;

public sealed class KustomizeOverlayTests
{
    [Fact]
    public async Task E2E_overlay_renders_expected_resources()
    {
        var root = FindRepositoryRoot();
        var result = await CommandRunner.RunRequiredAsync("kubectl", ["kustomize", "deploy/kubernetes/overlays/e2e"], root, TimeSpan.FromSeconds(30));
        var manifest = result.StandardOutput;

        Assert.Contains("name: formicae-api", manifest);
        Assert.Contains("kind: CronJob", manifest);
        Assert.Contains("name: formicae-postgres", manifest);
        Assert.Contains("kind: Role", manifest);
        Assert.Contains("PersistenceMode: Postgres", manifest);
        Assert.Contains("AgentMode: Fake", manifest);
        Assert.Contains("image: localhost/hhnl-formicae-api:e2e", manifest);
        Assert.Contains("image: localhost/hhnl-formicae-worker:e2e", manifest);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "hhnl.Formicae.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }
}

public sealed class KubernetesWorkflowE2ETests(KubernetesE2EFixture fixture) : IClassFixture<KubernetesE2EFixture>
{
    [Fact]
    public async Task Deployment_ComesUp_And_HealthEndpointResponds()
    {
        await WithDiagnosticsAsync(async () =>
        {
            using var portForward = await fixture.StartApiPortForwardAsync();
            using var http = new HttpClient { BaseAddress = portForward.BaseAddress };

            var response = await http.GetAsync("/healthz");

            response.EnsureSuccessStatusCode();
            Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        });
    }

    [Fact]
    public async Task Workflow_Completes_Through_Kubernetes_And_Persists()
    {
        await WithDiagnosticsAsync(async () =>
        {
            Guid workflowId;
            using (var portForward = await fixture.StartApiPortForwardAsync())
            using (var http = new HttpClient { BaseAddress = portForward.BaseAddress })
            {
                var startResponse = await http.PostAsJsonAsync("/api/workflows/github-issue", new
                {
                    issueUrl = "https://github.com/example/repo/issues/1",
                    repositoryUrl = "https://github.com/example/repo",
                    baseBranch = "main",
                    model = "e2e-model"
                });
                startResponse.EnsureSuccessStatusCode();

                using var startedJson = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
                workflowId = startedJson.RootElement.GetProperty("workflowId").GetGuid();

                using var completed = await WaitForCompletedWorkflowAsync(http, workflowId);
                AssertWorkflowCompleted(completed.RootElement);

                var runs = await http.GetFromJsonAsync<JsonElement[]>($"/api/workflows/{workflowId}/runs");
                Assert.NotNull(runs);
                Assert.Contains(runs, run => IsEnumValue(run.GetProperty("kind"), "Plan", 0));
                Assert.Contains(runs, run => IsEnumValue(run.GetProperty("kind"), "Implement", 1));
                Assert.Contains(runs, run => IsEnumValue(run.GetProperty("kind"), "CreatePullRequest", 2));

                var logs = await http.GetFromJsonAsync<JsonElement[]>($"/api/workflows/{workflowId}/logs");
                Assert.NotNull(logs);
                Assert.NotEmpty(logs);
            }

            await fixture.RestartApiAsync();

            using (var portForward = await fixture.StartApiPortForwardAsync())
            using (var http = new HttpClient { BaseAddress = portForward.BaseAddress })
            using (var persisted = JsonDocument.Parse(await http.GetStringAsync($"/api/workflows/{workflowId}")))
            {
                AssertWorkflowCompleted(persisted.RootElement);
            }
        });
    }

    private async Task<JsonDocument> WaitForCompletedWorkflowAsync(HttpClient http, Guid workflowId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        JsonDocument? latest = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            latest?.Dispose();
            latest = JsonDocument.Parse(await http.GetStringAsync($"/api/workflows/{workflowId}"));
            var root = latest.RootElement;

            if (IsEnumValue(root.GetProperty("status"), "Completed", 4))
            {
                return latest;
            }

            if (IsEnumValue(root.GetProperty("status"), "Failed", 5))
            {
                throw new InvalidOperationException($"Workflow failed: {root}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        using (latest)
        {
            throw new TimeoutException($"Workflow {workflowId} did not complete before timeout. Latest state: {latest?.RootElement.ToString()}");
        }
    }

    private static void AssertWorkflowCompleted(JsonElement workflow)
    {
        Assert.True(IsEnumValue(workflow.GetProperty("status"), "Completed", 4), workflow.ToString());
        Assert.True(IsEnumValue(workflow.GetProperty("currentStep"), "Done", 4), workflow.ToString());
        Assert.False(string.IsNullOrWhiteSpace(workflow.GetProperty("pullRequestUrl").GetString()));
    }

    private static bool IsEnumValue(JsonElement element, string stringValue, int numericValue)
        => element.ValueKind switch
        {
            JsonValueKind.String => string.Equals(element.GetString(), stringValue, StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Number => element.GetInt32() == numericValue,
            _ => false
        };

    private async Task WithDiagnosticsAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch
        {
            var diagnostics = await fixture.CaptureDiagnosticsAsync();
            Console.Error.WriteLine(diagnostics);
            throw;
        }
    }
}
