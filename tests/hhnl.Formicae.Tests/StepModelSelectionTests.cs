extern alias worker;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Infrastructure.OpenHands;
using hhnl.Formicae.Infrastructure.Prompts;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Tests;

public sealed class StepModelSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cli_process_is_terminated_on_timeout_or_success(bool succeeds)
    {
        var bash = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe") : "bash";
        using var process = new System.Diagnostics.Process { StartInfo = new(bash) {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(succeeds
            ? "read -r init; printf '%s\\n' '{\"id\":1,\"result\":{}}'; read -r initialized; read -r models; printf '%s\\n' '{\"id\":2,\"result\":{\"data\":[],\"nextCursor\":null}}'; read -r forever"
            : "read -r init; read -r forever");
        if (succeeds) Assert.Empty(await worker::CodexModelDiscovery.ExecuteAsync(process, TimeSpan.FromSeconds(5), default));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker::CodexModelDiscovery.ExecuteAsync(process, TimeSpan.FromMilliseconds(500), default));
        Assert.True(process.HasExited);
    }

    [Theory]
    [InlineData("step-model", "step-model")]
    [InlineData(null, "profile-model")]
    public async Task Runner_uses_named_configuration_credentials_and_model(string? model, string expected)
    {
        var settings = await SettingsAsync();
        var runtime = new Runtime();
        var runner = new OpenHandsAgentRunner(runtime, Options.Create(new RuntimeJobOptions()), Options.Create(new OpenHandsOptions()), settings);
        var result = await runner.StartAsync(new(Guid.NewGuid(), TaskRunKind.Plan, "plan", "https://example.com/repo", "main", model, AiSettingsId: "second"), default);
        Assert.Equal("second", result.AiSettingsId);
        Assert.Equal(expected, result.Model);
        Assert.Equal("second", runtime.Spec!.Environment["FORMICAE_AI_SETTINGS_ID"]);
        Assert.Equal(expected, runtime.Spec.Environment["FORMICAE_MODEL"]);
        Assert.Equal("{\"tokens\":\"second\"}", Assert.Single(runtime.Spec.SecretFiles!).Data["auth.json"]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("acp")]
    public async Task Runner_rejects_missing_or_unsupported_configuration(string id)
    {
        var runtime = new Runtime();
        var runner = new OpenHandsAgentRunner(runtime, Options.Create(new RuntimeJobOptions()), Options.Create(new OpenHandsOptions()), await SettingsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.StartAsync(new(Guid.NewGuid(), TaskRunKind.Plan, "plan", "https://example.com", "main", null, AiSettingsId: id), default));
        Assert.Null(runtime.Spec);
    }

    [Fact]
    public async Task Discovery_uses_selected_auth_without_repository_or_execution_capabilities()
    {
        var runtime = new Runtime();
        var service = new ModelDiscoveryService(runtime, Options.Create(new RuntimeJobOptions()), await SettingsAsync());
        var start = await service.StartAsync("second", default);
        Assert.Equal("Running", start.Status);
        Assert.Equal("ModelDiscovery", runtime.Spec!.Environment["FORMICAE_TASK_KIND"]);
        Assert.Equal("{\"tokens\":\"second\"}", Assert.Single(runtime.Spec.SecretFiles!).Data["auth.json"]);
        Assert.Null(runtime.Spec.ContextFiles);
        Assert.Null(runtime.Spec.ExecutionRequirements);
        Assert.Null(runtime.Spec.SecretEnvironment);
        Assert.DoesNotContain("FORMICAE_GIT_ACCESS_TOKEN", runtime.Spec.Environment.Keys);
        Assert.Equal(120, runtime.Spec.ExecutionPolicy!.TimeoutSeconds);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetStatusAsync("first", start.JobName!, default));
        runtime.Result = new(true, start.JobName!, "{\"type\":\"formicae.models\",\"models\":[{\"id\":\"cli-model\",\"displayName\":\"CLI Model\",\"isDefault\":true}]}\n", null);
        Assert.Equal("cli-model", Assert.Single((await service.GetStatusAsync("second", start.JobName!, default)).Models).Id);
    }

    [Theory]
    [InlineData("first")]
    [InlineData("acp")]
    public async Task Unsupported_discovery_does_not_launch_worker(string id)
    {
        var runtime = new Runtime();
        var service = new ModelDiscoveryService(runtime, Options.Create(new RuntimeJobOptions()), await SettingsAsync());
        Assert.Equal("Unsupported", (await service.StartAsync(id, default)).Status);
        Assert.Null(runtime.Spec);
    }

    [Theory]
    [InlineData(false, "SECRET")]
    [InlineData(true, "{\"type\":\"formicae.models\",invalid SECRET}")]
    [InlineData(true, "{\"type\":\"formicae.models\"}")]
    [InlineData(true, "{\"type\":\"formicae.models\",\"models\":[null]}")]
    public async Task Discovery_sanitizes_failed_and_malformed_results(bool success, string logs)
    {
        var runtime = new Runtime();
        var service = new ModelDiscoveryService(runtime, Options.Create(new RuntimeJobOptions()), await SettingsAsync());
        var start = await service.StartAsync("second", default);
        runtime.Result = new(success, start.JobName!, logs, "SECRET");
        var status = await service.GetStatusAsync("second", start.JobName!, default);
        Assert.Equal("Failed", status.Status);
        Assert.DoesNotContain("SECRET", status.FailureReason!);
    }

    [Fact]
    public async Task Cli_protocol_initializes_pages_and_uses_executable_model_identifier()
    {
        using var reader = new StringReader("""
            {"id":1,"result":{}}
            {"method":"notification"}
            {"id":2,"result":{"data":[{"id":"picker-id","model":"executable-id","displayName":"Model","isDefault":true}],"nextCursor":"page2"}}
            {"id":3,"result":{"data":[{"model":"other","displayName":"Other"}],"nextCursor":null}}
            """);
        using var writer = new StringWriter();
        var models = await worker::CodexModelDiscovery.DiscoverAsync(reader, writer, default);
        Assert.Equal(2, models.Count);
        Assert.Equal("executable-id", models[0].Id);
        Assert.True(models[0].IsDefault);
        Assert.Contains("\"method\":\"initialized\"", writer.ToString());
        Assert.Contains("\"cursor\":\"page2\"", writer.ToString());
        Assert.DoesNotContain("thread/start", writer.ToString());
    }

    [Theory]
    [InlineData("{\"id\":1,\"error\":{\"message\":\"auth\"}}")]
    [InlineData("not-json")]
    [InlineData("{\"id\":9,\"result\":{}}")]
    [InlineData("{\"id\":1,\"result\":{}}\n{\"id\":2,\"result\":{\"data\":[],\"nextCursor\":\"same\"}}\n{\"id\":3,\"result\":{\"data\":[],\"nextCursor\":\"same\"}}")]
    public async Task Cli_protocol_rejects_errors_malformed_output_and_invalid_pagination(string response)
    {
        using var reader = new StringReader(response);
        using var writer = new StringWriter();
        await Assert.ThrowsAnyAsync<Exception>(() => worker::CodexModelDiscovery.DiscoverAsync(reader, writer, default));
    }

    [Theory]
    [InlineData("override", "override", false)]
    [InlineData(null, "workflow-model", false)]
    [InlineData("override", "override", true)]
    [InlineData(null, "workflow-model", true)]
    public async Task Pinned_loop_steps_preserve_model_precedence_and_configuration(string? stepModel, string expected, bool retry)
    {
        var store = new InMemoryWorkflowStore();
        var definition = new WorkflowDefinition { Name = "models" };
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha2Schema, "plan",
            [new("plan", "builtins.plan", "plan", AiSettingsId: "second", Model: stepModel), new("exit", "builtins.implement")],
            Loops: [new("loop", ["plan"], 2, 2, "exit")]);
        var version = new WorkflowDefinitionVersion { WorkflowDefinitionId = definition.Id, Version = 1, DslSchemaVersion = document.Schema,
            DefinitionJson = WorkflowDefinitionJson.Serialize(document), IsEnabled = true, IsDefault = true };
        await store.EnsureDefaultWorkflowDefinitionAsync(definition, version, default);
        var workflow = await store.CreateWorkflowAsync(new Workflow { IssueUrl = "https://example.com/issues/1", RepositoryUrl = "https://example.com/repo",
            Model = "workflow-model", WorkflowDefinitionId = definition.Id, WorkflowDefinitionVersionId = version.Id, DslSchemaVersion = document.Schema, CurrentDefinitionStepId = "plan" }, default);
        var runner = new RecordingAgent { FailNext = retry };
        var orchestrator = new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), runner, new FilePromptRenderer());
        if (retry)
        {
            await orchestrator.AdvanceAsync(workflow, default);
            Assert.Equal(WorkflowStatus.Failed, workflow.Status);
            await new WorkflowService(store).RetryWorkflowAsync(workflow.Id, default);
        }
        for (var i = 0; i < 3; i++) await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal(retry ? 3 : 2, runner.Tasks.Count(task => task.Kind == TaskRunKind.Plan));
        Assert.All(runner.Tasks.Where(task => task.Kind == TaskRunKind.Plan), task => { Assert.Equal("second", task.AiSettingsId); Assert.Equal(expected, task.Model); });
        Assert.Equal("workflow-model", runner.Tasks.Last().Model);
        var events = await store.ListEventsAsync(workflow.Id, default);
        Assert.Contains(events, item => item.Type == "AgentSettingsResolved" && item.DetailsJson!.Contains(expected));
    }

    private static async Task<AiSettingsService> SettingsAsync()
    {
        var store = new InMemoryAiSettingsStore();
        await store.UpsertAsync(new AiSettings { Id = "first", Name = "First", Model = "wrong-model" }, default);
        await store.UpsertAsync(new AiSettings { Id = "second", Name = "Second", Model = "profile-model", AuthMethod = OpenHandsAuthMethods.CodexSubscription, CodexAuthJson = "{\"tokens\":\"second\"}" }, default);
        await store.UpsertAsync(new AiSettings { Id = "acp", Name = "ACP", AgentKind = AgentKinds.Acp }, default);
        return new(store, Options.Create(new OpenHandsOptions()), new SystemClock());
    }

    private sealed class Runtime : IJobRuntime
    {
        public RuntimeJobSpec? Spec;
        public RuntimeJobResult? Result;
        public Task<RuntimeJobStartResult> StartJobAsync(RuntimeJobSpec spec, CancellationToken token) { Spec = spec; return Task.FromResult(new RuntimeJobStartResult(spec.Name)); }
        public Task<RuntimeJobResult?> TryGetJobResultAsync(string id, CancellationToken token) => Task.FromResult(Result);
        public Task<string> ReadJobLogsAsync(string id, CancellationToken token) => Task.FromResult("");
    }

    private sealed class RecordingAgent : IAgentRunner
    {
        public List<AgentTask> Tasks = [];
        public bool FailNext;
        private readonly FakeAgentRunner inner = new();
        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken token)
        {
            Tasks.Add(task);
            if (FailNext) { FailNext = false; return Task.FromResult(new AgentRunStartResult("failed", new(false, "failed", "", "fixture failure"))); }
            return inner.StartAsync(task, token);
        }
        public Task<AgentRunResult?> TryGetResultAsync(string id, CancellationToken token) => inner.TryGetResultAsync(id, token);
    }
}
