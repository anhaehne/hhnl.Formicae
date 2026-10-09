using System.Reflection;
using System.Text.Json;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class IssueCommentTaskTests
{
    private const string Repository = "https://github.com/acme/repo";
    private static WorkflowIssueCommentSettings Settings(int id = 42, string text = "Hello **world**\n✓") => new(
        new Dictionary<string, JsonElement> { ["issueId"] = JsonSerializer.SerializeToElement(id), ["text"] = JsonSerializer.SerializeToElement(text) });
    private static WorkflowDefinitionStep Comment(string id = "comment", string? next = null, WorkflowIssueCommentSettings? settings = null) =>
        new(id, IssueCommentDefinitions.Uses, next, IssueComment: settings ?? Settings());
    private static WorkflowDefinitionStep Created(string next = "comment") => new("created", "github.issue-created", next,
        Event: WorkflowEventDefinitions.Configuration(new IssueEventSettings(true, [Guid.NewGuid()])));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Comment_resolves_literals_or_event_outputs_and_runs_without_a_worker(bool bound, bool graph)
    {
        var settings = bound ? new WorkflowIssueCommentSettings(
            new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("Hello **world**\n✓") },
            new Dictionary<string, CustomTaskInputBinding> { ["issueId"] = new("created", "issueId") }) : Settings();
        var comment = Comment(settings: settings);
        IReadOnlyList<WorkflowDefinitionStep> steps = bound ? [Created(), comment] : [comment];
        if (graph) steps = [Created("fork"), Comment("fork", "comment") with { NextStepIds = ["other"] }, comment, Comment("other")];
        var (store, workflow) = await Setup(steps, bound ? "" : "comment", bound);
        var platform = DispatchProxy.Create<IDevOpsPlatform, CommentPlatform>();
        var recorder = (CommentPlatform)(object)platform;
        var orchestrator = Orchestrator(store, new Factory(platform));
        for (var i = 0; i < 5 && workflow.Status != WorkflowStatus.Completed; i++) await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal(graph ? 3 : 1, recorder.Comments.Count);
        Assert.All(recorder.Comments, item => { Assert.Equal(42, item.Issue.Number); Assert.Equal(Repository, item.Issue.RepositoryUrl); Assert.Equal("Hello **world**\n✓", item.Text); });
        var run = (await store.ListTaskRunsAsync(workflow.Id, default)).Single(run => run.DefinitionStepId == "comment");
        Assert.Null(run.ExternalId); Assert.Equal(TaskRunStatus.Succeeded, run.Status);
        Assert.Equal(42, run.ToResponse().IssueCommentExecution!.Inputs["issueId"].GetInt32());
        if (bound) Assert.Equal("created", run.ToResponse().IssueCommentExecution!.Provenance["issueId"].StepId);
        await Orchestrator(store, new Factory(platform)).AdvanceAsync(workflow, default);
        Assert.Equal(graph ? 3 : 1, recorder.Comments.Count);
    }

    [Fact]
    public async Task Comment_consumes_text_from_an_upstream_script()
    {
        var settings = new WorkflowIssueCommentSettings(new Dictionary<string, JsonElement> { ["issueId"] = JsonSerializer.SerializeToElement(42) },
            new Dictionary<string, CustomTaskInputBinding> { ["text"] = new("script", "output") });
        var (store, workflow) = await Setup([new("script", WorkflowExecutionExtensions.ScriptUses, "comment", Script: new("printf text")), Comment(settings: settings)], "script", false);
        var platform = DispatchProxy.Create<IDevOpsPlatform, CommentPlatform>();
        var orchestrator = new WorkflowOrchestrator(store, DispatchProxy.Create<IWorkItemProvider, Forbidden>(), DispatchProxy.Create<ISourceControlProvider, Forbidden>(),
            new TextRunner(), DispatchProxy.Create<IPromptRenderer, Forbidden>(), devOpsPlatforms: new Factory(platform));
        await orchestrator.AdvanceAsync(workflow, default); await orchestrator.AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal("Generated comment", Assert.Single(((CommentPlatform)(object)platform).Comments).Text);
        var run = (await store.ListTaskRunsAsync(workflow.Id, default)).Single(run => run.Kind == TaskRunKind.AddIssueComment);
        Assert.Equal("script", run.ToResponse().IssueCommentExecution!.Provenance["text"].StepId);
    }

    [Fact]
    public async Task Failed_comment_retries_with_frozen_inputs_and_retains_event_snapshot()
    {
        var settings = new WorkflowIssueCommentSettings(new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("Frozen text") },
            new Dictionary<string, CustomTaskInputBinding> { ["issueId"] = new("created", "issueId") });
        var (store, workflow) = await Setup([Created(), Comment(settings: settings)], "", true);
        var platform = DispatchProxy.Create<IDevOpsPlatform, CommentPlatform>(); var recorder = (CommentPlatform)(object)platform; recorder.Fail = true;
        await Orchestrator(store, new Factory(platform)).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status);
        var runs = await store.ListTaskRunsAsync(workflow.Id, default); var run = runs.Single(run => run.Kind == TaskRunKind.AddIssueComment);
        Assert.Contains("provider failure", run.FailureReason);
        var snapshot = runs.Single(run => run.Kind == TaskRunKind.Event).StructuredOutputsJson;
        var prepared = run.CustomTaskExecutionJson; var attempt = run.ExecutionAttemptId;
        await new WorkflowService(store).RetryWorkflowAsync(workflow.Id, default);
        Assert.Equal(prepared, run.CustomTaskExecutionJson); Assert.NotEqual(attempt, run.ExecutionAttemptId);
        Assert.Equal(snapshot, runs.Single(run => run.Kind == TaskRunKind.Event).StructuredOutputsJson);
        recorder.Fail = false;
        await Orchestrator(store, new Factory(platform)).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal("Frozen text", Assert.Single(recorder.Comments).Text);
        Assert.Equal(prepared, Assert.Single(await store.ListTaskRunAttemptsAsync(workflow.Id, default)).CustomTaskExecutionJson);
    }

    [Fact]
    public async Task Retry_of_later_failed_task_does_not_repeat_successful_comment()
    {
        var (store, workflow) = await Setup([Comment(next: "later"), Comment("later", settings: Settings(text: "Later"))], "comment", false);
        var platform = DispatchProxy.Create<IDevOpsPlatform, CommentPlatform>(); var recorder = (CommentPlatform)(object)platform;
        var factory = new Factory(platform); var orchestrator = Orchestrator(store, factory);
        await orchestrator.AdvanceAsync(workflow, default); recorder.Fail = true;
        await orchestrator.AdvanceAsync(workflow, default); Assert.Equal(WorkflowStatus.Failed, workflow.Status);
        await new WorkflowService(store).RetryWorkflowAsync(workflow.Id, default); recorder.Fail = false;
        await Orchestrator(store, factory).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status); Assert.Equal(2, recorder.Comments.Count);
        Assert.Equal("Later", recorder.Comments[1].Text);
    }

    [Theory]
    [InlineData(0, "Text")]
    [InlineData(-1, "Text")]
    [InlineData(1, "")]
    [InlineData(1, "   ")]
    public void Invalid_literal_inputs_are_rejected_before_execution(int id, string text)
        => Assert.False(new WorkflowDefinitionValidator().Validate(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "comment", [Comment(settings: Settings(id, text))])).IsValid);

    [Fact]
    public void Event_bindings_require_the_selected_entrypoint_and_matching_types()
    {
        var settings = new WorkflowIssueCommentSettings(new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("text") },
            new Dictionary<string, CustomTaskInputBinding> { ["issueId"] = new("created", "issueId") });
        var doc = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "", [Created(), Comment(settings: settings)]);
        Assert.True(CustomTaskDefinitions.ValidateRuntime(doc).IsValid);
        var alternate = doc with { StartStepId = "manual", Steps = [..doc.Steps, new("manual", "builtins.start", "comment", Event: WorkflowEventDefinitions.Configuration(new ManualEventSettings()))] };
        Assert.False(CustomTaskDefinitions.ValidateRuntime(alternate).IsValid);
        var wrong = doc with { Steps = [Created(), Comment(settings: settings with { Bindings = new Dictionary<string, CustomTaskInputBinding> { ["issueId"] = new("created", "issue") } })] };
        Assert.False(CustomTaskDefinitions.ValidateRuntime(wrong).IsValid);
    }

    [Fact]
    public async Task Selected_event_outputs_are_available_in_each_loop_iteration()
    {
        var settings = new WorkflowIssueCommentSettings(new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("Iteration") },
            new Dictionary<string, CustomTaskInputBinding> { ["issueId"] = new("created", "issueId") });
        var body = Comment(settings: settings) with { NextStepId = "repeat", NextStepPort = "return" };
        var (store, workflow) = await Setup([Created("repeat"), new("repeat", WorkflowNodeDefinitions.LoopUses, "exit", Loop: new("comment", 2, 2)), body, Comment("exit")], "", true);
        var platform = DispatchProxy.Create<IDevOpsPlatform, CommentPlatform>(); var factory = new Factory(platform);
        for (var i = 0; i < 5 && workflow.Status != WorkflowStatus.Completed; i++) await Orchestrator(store, factory).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status); Assert.Equal(3, ((CommentPlatform)(object)platform).Comments.Count);
        var runs = (await store.ListTaskRunsAsync(workflow.Id, default)).Where(run => run.DefinitionStepId == "comment").ToArray();
        Assert.Equal(new int?[] { 1, 2 }, runs.Select(run => run.LoopIteration).Order().ToArray());
        Assert.All(runs, run => Assert.Equal("created", run.ToResponse().IssueCommentExecution!.Provenance["issueId"].StepId));
    }

    [Fact]
    public async Task Wrong_provider_fails_without_posting()
    {
        var settings = new WorkflowIssueCommentSettings(new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("text") },
            new Dictionary<string, CustomTaskInputBinding> { ["issueId"] = new("created", "issueId") });
        var (store, workflow) = await Setup([Created(), Comment(settings: settings)], "", true);
        var platform = DispatchProxy.Create<IDevOpsPlatform, CommentPlatform>(); var recorder = (CommentPlatform)(object)platform;
        await Orchestrator(store, new Factory(platform, DevOpsProviderType.Gitea)).AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status); Assert.Empty(recorder.Comments);
        Assert.Contains("GitHub", workflow.FailureReason);
    }

    private static async Task<(InMemoryWorkflowStore, Workflow)> Setup(IReadOnlyList<WorkflowDefinitionStep> steps, string start, bool bound)
    {
        var store = new InMemoryWorkflowStore(); var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, start, steps);
        Assert.True(new WorkflowDefinitionValidator().Validate(document).IsValid); Assert.True(CustomTaskDefinitions.ValidateRuntime(document).IsValid);
        var definition = await store.CreateWorkflowDefinitionAsync(new() { Name = "Comments" }, default);
        var version = await store.CreateWorkflowDefinitionVersionAsync(new() { WorkflowDefinitionId = definition.Id, Version = 1, DslSchemaVersion = document.Schema, DefinitionJson = WorkflowDefinitionJson.Serialize(document), IsEnabled = true }, default);
        IReadOnlyDictionary<string, JsonElement>? outputs = bound ? new Dictionary<string, JsonElement> { ["issueId"] = JsonSerializer.SerializeToElement(42),
            ["issue"] = JsonSerializer.SerializeToElement(JsonSerializer.Serialize(new { number = 42, body = new string('x', 70000), nested = new { unknown = true } })) } : null;
        var summary = await new WorkflowService(store, workflowDefinitions: new WorkflowDefinitionService(store, new())).StartGitHubIssueWorkflowAsync(new(Repository + "/issues/42", Repository, "main", null, definition.Id, version.Id), default, bound ? "created" : null, outputs);
        return (store, (await store.GetWorkflowAsync(summary.WorkflowId, default))!);
    }
    private static WorkflowOrchestrator Orchestrator(IWorkflowStore store, Factory factory) => new(store,
        DispatchProxy.Create<IWorkItemProvider, Forbidden>(), DispatchProxy.Create<ISourceControlProvider, Forbidden>(), DispatchProxy.Create<IAgentRunner, Forbidden>(), DispatchProxy.Create<IPromptRenderer, Forbidden>(), devOpsPlatforms: factory);
    public class Forbidden : DispatchProxy
    { protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new InvalidOperationException("Unexpected worker/provider call: " + method!.Name); }
    public class CommentPlatform : DispatchProxy
    {
        public bool Fail; public List<(DevOpsIssueReference Issue, string Text)> Comments = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != nameof(IDevOpsPlatform.CreateIssueCommentAsync)) throw new InvalidOperationException("Unexpected API call");
            if (Fail) return Task.FromException(new InvalidOperationException("provider failure"));
            Comments.Add(((DevOpsIssueReference)args![0]!, (string)args[1]!)); return Task.CompletedTask;
        }
    }
    private sealed class TextRunner : IAgentRunner
    {
        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken token)
        { Assert.Equal(TaskRunKind.Script, task.Kind); return Task.FromResult(new AgentRunStartResult("script-worker", new(true, "script-worker", "Generated comment", null, ExitCode: 0))); }
        public Task<AgentRunResult?> TryGetResultAsync(string id, CancellationToken token) => Task.FromResult<AgentRunResult?>(null);
    }
    private sealed class Factory(IDevOpsPlatform platform, DevOpsProviderType provider = DevOpsProviderType.GitHub) : IDevOpsPlatformFactory
    {
        public Task<DevOpsPlatformContext> CreateForRepositoryAsync(string repositoryUrl, CancellationToken token)
        {
            Assert.Equal(Repository, repositoryUrl);
            return Task.FromResult(new DevOpsPlatformContext(new() { ProviderType = provider, DisplayName = "Test" },
                new() { Owner = "acme", Name = "repo", RepositoryUrl = Repository }, new(provider, "acme", "repo", Repository, new("https://github.com")), platform));
        }
    }
}
