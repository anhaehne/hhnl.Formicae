using System.Net.Http.Json;
using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
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
        Assert.Contains("name: formicae-postgres", manifest);
        Assert.Contains("kind: Role", manifest);
        Assert.Contains("PersistenceMode: Postgres", manifest);
        Assert.Contains("AgentMode: Fake", manifest);
        Assert.Contains("JobRuntime: Kubernetes", manifest);
        Assert.Contains("image: localhost/hhnl-formicae-api:e2e", manifest);
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
                var definitionResponse = await http.PostAsJsonAsync("/api/workflow-definitions", new { name = "Post-upgrade loop" });
                definitionResponse.EnsureSuccessStatusCode();
                var definition = await definitionResponse.Content.ReadFromJsonAsync<JsonElement>();
                var definitionId = definition.GetProperty("id").GetGuid();
                var document = DefaultWorkflowDefinitions.CreateMvpDocument() with
                {
                    Schema = DefaultWorkflowDefinitions.V1Alpha3Schema,
                    StartStepId = "planning",
                    Steps = [.. DefaultWorkflowDefinitions.CreateMvpDocument().Steps.Select(step =>
                        step.Id == "plan" ? step with { NextStepId = "planning", NextStepPort = "return" } : step),
                        new("planning", WorkflowNodeDefinitions.LoopUses, "implement", Loop: new("plan", 2, 3))]
                };
                var versionResponse = await http.PostAsJsonAsync($"/api/workflow-definitions/{definitionId}/versions",
                    new CreateWorkflowDefinitionVersionRequest(null, true, false, document));
                versionResponse.EnsureSuccessStatusCode();
                var version = await versionResponse.Content.ReadFromJsonAsync<JsonElement>();
                var startResponse = await http.PostAsJsonAsync("/api/workflows/github-issue", new
                {
                    issueUrl = "https://github.com/example/repo/issues/1",
                    repositoryUrl = "https://github.com/example/repo",
                    baseBranch = "main",
                    model = "e2e-model",
                    workflowDefinitionId = definitionId,
                    workflowDefinitionVersionId = version.GetProperty("id").GetGuid()
                });
                startResponse.EnsureSuccessStatusCode();

                using var startedJson = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
                workflowId = startedJson.RootElement.GetProperty("workflowId").GetGuid();

                using var completed = await WaitForCompletedWorkflowAsync(http, workflowId);
                AssertWorkflowCompleted(completed.RootElement);

                var runs = await http.GetFromJsonAsync<JsonElement[]>($"/api/workflows/{workflowId}/runs");
                Assert.NotNull(runs);
                Assert.DoesNotContain(runs, run => run.GetProperty("definitionStepId").GetString() == "planning");
                Assert.All(runs, run => Assert.False(string.IsNullOrWhiteSpace(run.GetProperty("definitionStepId").GetString())));
                Assert.Equal(runs.Length, runs.Select(run => (run.GetProperty("definitionStepId").GetString(), run.GetProperty("loopIteration").ToString())).Distinct().Count());
                var planningRuns = runs.Where(run => IsEnumValue(run.GetProperty("kind"), "Plan", 0)).ToArray();
                Assert.Equal(new[] { 1, 2 }, planningRuns.Select(run => run.GetProperty("loopIteration").GetInt32()).Order());
                Assert.Contains(runs, run => IsEnumValue(run.GetProperty("kind"), "Implement", 1));
                Assert.Contains(runs, run => IsEnumValue(run.GetProperty("kind"), "CreatePullRequest", 2));
                Assert.Contains(runs, run => IsEnumValue(run.GetProperty("kind"), "AddressComments", 3));

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

    [Fact]
    public async Task UpgradeFrom074_PreservesLegacyHistory()
    {
        await WithDiagnosticsAsync(async () =>
        {
            using var portForward = await fixture.StartApiPortForwardAsync();
            using var http = new HttpClient { BaseAddress = portForward.BaseAddress };
            const string id = "74000000-0000-0000-0000-000000000001";
            (await http.GetAsync("/healthz")).EnsureSuccessStatusCode();
            var workflow = await http.GetFromJsonAsync<JsonElement>($"/api/workflows/{id}");
            Assert.Equal("addressComments", workflow.GetProperty("currentDefinitionStepId").GetString());
            var runs = await http.GetFromJsonAsync<JsonElement[]>($"/api/workflows/{id}/runs");
            Assert.Equal(4, runs!.Length);
            Assert.Equal(new[] { "addressComments", "createPullRequest", "implement", "plan" },
                runs.Select(run => run.GetProperty("definitionStepId").GetString()).Order());
            Assert.All(runs, run =>
            {
                Assert.StartsWith("74000000-", run.GetProperty("id").GetString());
                Assert.Equal(JsonValueKind.Null, run.GetProperty("loopIteration").ValueKind);
                Assert.StartsWith("preserved output:", run.GetProperty("output").GetString());
                Assert.Equal(DateTimeOffset.Parse("2026-07-01T01:00:00Z"), run.GetProperty("createdAt").GetDateTimeOffset());
            });
            var logs = await http.GetStringAsync($"/api/workflows/{id}/logs");
            Assert.Contains("preserved retry log", logs);
            var events = await http.GetStringAsync($"/api/workflows/{id}/events");
            Assert.Contains("preserved retry event", events);
        });
    }

    [Fact]
    public async Task FailedRollout_CollectsPreviousContainerLogs()
    {
        var diagnostics = await fixture.ExerciseFailedRolloutAsync();
        Assert.Contains("previous logs (if available)", diagnostics);
        var previousSection = diagnostics[(diagnostics.IndexOf("previous logs (if available)", StringComparison.Ordinal))..];
        Assert.Contains("intentional-rollout-failure", previousSection);
        Assert.Contains("diagnostics-api", diagnostics);
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

            if (IsEnumValue(root.GetProperty("status"), "Completed", 5))
            {
                return latest;
            }

            if (IsEnumValue(root.GetProperty("status"), "Failed", 6))
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
        Assert.True(IsEnumValue(workflow.GetProperty("status"), "Completed", 5), workflow.ToString());
        Assert.True(IsEnumValue(workflow.GetProperty("currentStep"), "Done", 5), workflow.ToString());
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
