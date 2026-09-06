using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class PersonaOrchestratorTests
{
    [Fact]
    public async Task Sequential_ai_tasks_retry_with_pinned_persona_after_catalog_delete_and_pr_stays_direct()
    {
        var catalog = new PersonaService(new InMemoryPersonaStore());
        var persona = await catalog.CreateAsync(new("Reviewer", "frozen instructions", "Concise", "Inspect first"), default);
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "plan", [
            new("plan", "builtins.plan", "implement", Model: "plan-model", AiSettingsId: "plan-ai"),
            new("implement", "builtins.implement", "pr"), new("pr", "builtins.create-pull-request", "review"),
            new("review", "builtins.address-comments") ], DefaultPersonaId: persona.Id);
        var (store, workflow) = await SetupAsync(document, catalog);
        await catalog.UpdateAsync(persona.Id, new(1, "Changed", "mutable new instructions"), default);
        await catalog.DeleteAsync(persona.Id, 2, default);
        var agent = new RecordingAgent { FailFirst = true };
        WorkflowOrchestrator Restart() => new(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new BasePrompt());
        await Restart().AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Failed, workflow.Status);
        await new WorkflowService(store).RetryWorkflowAsync(workflow.Id, default);
        for (var i = 0; i < 6; i++) await Restart().AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal(new[] { TaskRunKind.Plan, TaskRunKind.Plan, TaskRunKind.Implement, TaskRunKind.AddressComments }, agent.Tasks.Select(task => task.Kind));
        Assert.All(agent.Tasks, task =>
        {
            Assert.Contains("frozen instructions", task.Prompt); Assert.DoesNotContain("mutable new", task.Prompt);
            Assert.Equal(1, task.Prompt.Split("## Persona guidance", StringSplitOptions.None).Length - 1);
        });
        Assert.Equal(agent.Tasks[0].Prompt, agent.Tasks[1].Prompt);
        Assert.All(agent.Tasks.Take(2), task => { Assert.Equal("plan-model", task.Model); Assert.Equal("plan-ai", task.AiSettingsId); });
        Assert.NotEmpty(agent.Tasks[^1].ContextFiles!);
        var audits = (await store.ListEventsAsync(workflow.Id, default)).Where(evt => evt.Type == "AgentSettingsResolved").ToArray();
        Assert.Equal(4, audits.Length);
        Assert.All(audits, evt =>
        {
            using var details = JsonDocument.Parse(evt.DetailsJson!);
            Assert.Equal(persona.Id, details.RootElement.GetProperty("personaId").GetString());
            Assert.Equal(1, details.RootElement.GetProperty("personaRevision").GetInt32());
            Assert.Equal("Reviewer", details.RootElement.GetProperty("personaName").GetString());
        });
        Assert.Single(await store.ListTaskRunsAsync(workflow.Id, default), run => run.Kind == TaskRunKind.CreatePullRequest);
        Assert.DoesNotContain(agent.Tasks, task => task.Kind == TaskRunKind.CreatePullRequest);
    }

    [Fact]
    public async Task Parallel_preparation_preserves_attempt_identity_model_and_default_opt_out_on_uncertain_retry()
    {
        var catalog = new PersonaService(new InMemoryPersonaStore());
        var persona = await catalog.CreateAsync(new("Planner", "parallel persona"), default);
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "group", [
            new("group", "builtins.parallel", "finish", Parallel: new(["a", "b"])),
            new("a", "builtins.plan", "group", Model: "branch-model", AiSettingsId: "branch-ai", NextStepPort: "join"),
            new("b", "builtins.plan", "group", NextStepPort: "join", PersonaId: "default"), new("finish", "builtins.plan") ], DefaultPersonaId: persona.Id);
        var (store, workflow) = await SetupAsync(document, catalog);
        await catalog.DeleteAsync(persona.Id, 1, default);
        var agent = new RecordingAgent { UncertainFirst = true };
        WorkflowOrchestrator Restart() => new(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new BasePrompt());
        for (var i = 0; i < 5; i++) await Restart().AdvanceAsync(workflow, default);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        var branchA = agent.Tasks.Where(task => task.Prompt.StartsWith("BASE Plan/a", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, branchA.Length);
        Assert.Equal(branchA[0].ExecutionAttemptId, branchA[1].ExecutionAttemptId); Assert.NotNull(branchA[0].ExecutionAttemptId);
        Assert.Equal(branchA[0].Prompt, branchA[1].Prompt);
        Assert.All(branchA, task =>
        {
            Assert.Equal("branch-model", task.Model); Assert.Equal("branch-ai", task.AiSettingsId);
            Assert.Equal(1, task.Prompt.Split("## Persona guidance", StringSplitOptions.None).Length - 1);
        });
        var branchB = Assert.Single(agent.Tasks, task => task.Prompt.StartsWith("BASE Plan/b", StringComparison.Ordinal));
        Assert.Equal("BASE Plan/b", branchB.Prompt); Assert.NotNull(branchB.ExecutionAttemptId);
        var bRun = (await store.ListTaskRunsAsync(workflow.Id, default)).Single(run => run.DefinitionStepId == "b");
        using var audit = JsonDocument.Parse((await store.ListEventsAsync(workflow.Id, default)).Single(evt => evt.Type == "AgentSettingsResolved" && evt.TaskRunId == bRun.Id).DetailsJson!);
        Assert.Equal("default", audit.RootElement.GetProperty("personaId").GetString());
    }

    [Fact]
    public async Task Legacy_default_prompt_is_byte_for_byte_unchanged()
    {
        var store = new InMemoryWorkflowStore();
        var definition = await store.CreateWorkflowDefinitionAsync(new() { Name = "Legacy version" }, default);
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "plan", [new("plan", "builtins.plan")]);
        var version = await store.CreateWorkflowDefinitionVersionAsync(new() { WorkflowDefinitionId = definition.Id, Version = 1,
            DslSchemaVersion = document.Schema, DefinitionJson = WorkflowDefinitionJson.Serialize(document), IsEnabled = true }, default);
        var workflow = await store.CreateWorkflowAsync(new() { IssueUrl = "https://example.test/issues/1", RepositoryUrl = "https://example.test/repo",
            WorkflowDefinitionId = definition.Id, WorkflowDefinitionVersionId = version.Id, CurrentDefinitionStepId = "plan" }, default);
        var agent = new RecordingAgent();
        await new WorkflowOrchestrator(store, new FakeWorkItemProvider(), new FakeSourceControlProvider(), agent, new BasePrompt()).AdvanceAsync(workflow, default);
        Assert.Equal("BASE Plan/plan", Assert.Single(agent.Tasks).Prompt);
    }

    private static async Task<(InMemoryWorkflowStore Store, Workflow Workflow)> SetupAsync(WorkflowDefinitionDocument document, PersonaService? personas)
    {
        var store = new InMemoryWorkflowStore(); var definitions = new WorkflowDefinitionService(store, new(), personas: personas);
        var definition = await definitions.CreateAsync(new("Persona workflow"), default);
        var version = await definitions.CreateVersionAsync(definition.Id, new(null, true, false, document), default);
        var started = await new WorkflowService(store, workflowDefinitions: definitions).StartGitHubIssueWorkflowAsync(new("https://example.test/issues/1", "https://example.test/repo", null, "workflow-model", WorkflowDefinitionId: definition.Id, WorkflowDefinitionVersionId: version.Id), default);
        return (store, (await store.GetWorkflowAsync(started.WorkflowId, default))!);
    }
    private sealed class BasePrompt : IPromptRenderer
    {
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? issue, CancellationToken token) => Task.FromResult($"BASE {kind}/{workflow.CurrentDefinitionStepId}");
        public Task<string> RenderAsync(TaskRunKind kind, Workflow workflow, WorkItem? issue, IReadOnlyList<PullRequestComment> comments, CancellationToken token) => RenderAsync(kind, workflow, issue, token);
    }
    private sealed class RecordingAgent : IAgentRunner
    {
        public List<AgentTask> Tasks = [];
        public bool FailFirst;
        public bool UncertainFirst;
        public Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken token)
        {
            Tasks.Add(task);
            if (UncertainFirst) { UncertainFirst = false; throw new AgentLaunchUncertainException("lost response", new HttpRequestException()); }
            var success = !FailFirst; FailFirst = false; var id = task.ExecutionAttemptId?.ToString() ?? Tasks.Count.ToString();
            return Task.FromResult(new AgentRunStartResult(id, new(success, id, "recorded plan", success ? null : "retry me")));
        }
        public Task<AgentRunResult?> TryGetResultAsync(string id, CancellationToken token) => Task.FromResult<AgentRunResult?>(null);
    }
}
