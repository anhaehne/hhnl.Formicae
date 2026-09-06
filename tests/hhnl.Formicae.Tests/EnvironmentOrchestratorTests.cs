using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Infrastructure.OpenHands;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Tests;

public sealed class EnvironmentOrchestratorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_and_custom_tasks_receive_pinned_profile_and_bounded_profile_audit(bool custom)
    {
        var customSnapshot = new CustomTaskSnapshot("task", 1, "Task", "", "custom prompt", [], new(TimeoutSeconds: 43));
        var step = new WorkflowDefinitionStep("step", custom ? CustomTaskDefinitions.Uses : "builtins.plan",
            CustomTask: custom ? new("task", Snapshot: customSnapshot) : null);
        var (store, workflow) = await SetupAsync([step], "step"); var agent = new Agent();
        await new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new Prompt()).AdvanceAsync(workflow, default);
        var task = Assert.Single(agent.Tasks);
        Assert.Equal("profile", task.EnvironmentSnapshot!.Id); Assert.Equal(2, task.EnvironmentSnapshot.Revision);
        Assert.Equal(60, task.EnvironmentSnapshot.Configuration.Runtime!.TimeoutLimitSeconds);
        if (custom) { Assert.Equal(43, task.TimeoutSeconds); Assert.NotNull(task.ExecutionAttemptId); }
        AssertProfileAudit(await store.ListEventsAsync(workflow.Id, default));
    }

    [Fact]
    public async Task Parallel_reattach_after_platform_change_audits_pinned_profile_not_recomputed_job_facts()
    {
        var (store, workflow) = await SetupAsync([
            new("group", WorkflowParallelDefinitions.Uses, "finish", Parallel: new(["a", "b"])),
            new("a", "builtins.plan", "group", NextStepPort: "join"), new("b", "builtins.plan", "group", NextStepPort: "join"),
            new("finish", "builtins.plan")], "group");
        var runtime = new DurableRuntime(); var options = new RuntimeJobOptions { Image = "worker:original" };
        var runner = new OpenHandsAgentRunner(runtime, Options.Create(options), Options.Create(new OpenHandsOptions()));
        WorkflowOrchestrator Restart() => new(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), runner, new Prompt());
        await Restart().AdvanceAsync(workflow, default);
        var attempt = runtime.Specs[0].Name; Assert.Equal("worker:original", runtime.Jobs[attempt].Image);
        options.Image = "worker:changed";
        await Restart().AdvanceAsync(workflow, default);
        Assert.Equal(2, runtime.Specs.Count(spec => spec.Name == attempt));
        Assert.Equal("worker:original", runtime.Jobs[attempt].Image);
        Assert.Contains(runtime.Specs, spec => spec.Name == attempt && spec.Image == "worker:changed");
        Assert.All(runtime.Specs, spec => Assert.Equal(60, spec.TimeoutLimitSeconds));
        AssertProfileAudit(await store.ListEventsAsync(workflow.Id, default));
        var assigned = (await store.ListTaskRunsAsync(workflow.Id, default)).Where(run => run.ExternalId is not null).ToArray();
        Assert.Equal(2, assigned.Length); Assert.All(assigned, run => Assert.NotNull(run.ExecutionAttemptId));
    }

    private static void AssertProfileAudit(IReadOnlyList<WorkflowEvent> events)
    {
        var audits = events.Where(item => item.Type == "AgentSettingsResolved").ToArray(); Assert.NotEmpty(audits);
        Assert.All(audits, item =>
        {
            Assert.NotNull(item.TaskRunId);
            using var json = JsonDocument.Parse(item.DetailsJson!);
            var profile = json.RootElement.GetProperty("environment");
            Assert.Equal(new[] { "id", "name", "revision", "timeoutLimitSeconds" }, profile.EnumerateObject().Select(property => property.Name).Order());
            Assert.Equal("profile", profile.GetProperty("id").GetString()); Assert.Equal(2, profile.GetProperty("revision").GetInt32());
            Assert.Equal(60, profile.GetProperty("timeoutLimitSeconds").GetInt32());
            Assert.DoesNotContain("worker:", item.DetailsJson!); Assert.DoesNotContain("effectiveTimeout", item.DetailsJson!);
        });
    }

    private static async Task<(InMemoryWorkflowStore, Workflow)> SetupAsync(IReadOnlyList<WorkflowDefinitionStep> steps, string start)
    {
        var store = new InMemoryWorkflowStore(); var definition = await store.CreateWorkflowDefinitionAsync(new() { Name = "Environment" }, default);
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, start, steps,
            DefaultEnvironmentId: "profile", DefaultEnvironmentSnapshot: new("profile", 2, "Bounded", "", new() { Runtime = new(60) }));
        var version = await store.CreateWorkflowDefinitionVersionAsync(new() { WorkflowDefinitionId = definition.Id, Version = 1,
            DefinitionJson = WorkflowDefinitionJson.Serialize(document), DslSchemaVersion = document.Schema, IsEnabled = true }, default);
        var workflow = await store.CreateWorkflowAsync(new() { IssueUrl = "https://example.test/issues/1", RepositoryUrl = "https://example.test/repo",
            WorkflowDefinitionId = definition.Id, WorkflowDefinitionVersionId = version.Id, CurrentDefinitionStepId = start, Status = WorkflowStatus.Planning }, default);
        return (store, workflow);
    }
    private sealed class Prompt : IPromptRenderer
    {
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? issue, CancellationToken token) => Task.FromResult("prompt");
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? issue, IReadOnlyList<PullRequestComment> comments, CancellationToken token) => Task.FromResult("prompt");
    }
    private sealed class Agent : IAgentRunner
    {
        public List<AgentTask> Tasks = [];
        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken token) { Tasks.Add(task); return Task.FromResult(new AgentRunStartResult("job")); }
        public Task<AgentRunResult?> TryGetResultAsync(string id, CancellationToken token) => Task.FromResult<AgentRunResult?>(null);
    }
    private sealed class DurableRuntime : IJobRuntime
    {
        public List<RuntimeJobSpec> Specs = [];
        public Dictionary<string, RuntimeJobSpec> Jobs = [];
        private bool loseFirst = true;
        public Task<RuntimeJobStartResult> StartJobAsync(RuntimeJobSpec spec, CancellationToken token)
        {
            Specs.Add(spec); Jobs.TryAdd(spec.Name, spec);
            if (loseFirst) { loseFirst = false; throw new HttpRequestException("lost acceptance response"); }
            return Task.FromResult(new RuntimeJobStartResult(spec.Name));
        }
        public Task<RuntimeJobResult?> TryGetJobResultAsync(string id, CancellationToken token) => Task.FromResult<RuntimeJobResult?>(null);
        public Task<string> ReadJobLogsAsync(string id, CancellationToken token) => Task.FromResult("");
    }
}
