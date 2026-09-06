using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Infrastructure.OpenHands;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Tests;

public sealed class CustomTaskCallbackTests
{
    [Theory]
    [InlineData(TaskRunStatus.Running)]
    [InlineData(TaskRunStatus.Succeeded)]
    [InlineData(TaskRunStatus.Failed)]
    public async Task Custom_messages_are_bounded_logs_and_never_replace_final_output_or_status(TaskRunStatus status)
    {
        var (store, workflow, first, second) = await SetupAsync();
        first.Status = status; first.Output = "authoritative";
        await store.UpsertTaskRunAsync(first, default);
        var service = new WorkerAgentMessageService(store);
        Assert.True(await service.RecordAsync(new(workflow.Id, "Custom", "first", "stdout",
            "{\"type\":\"agent_message\",\"message\":\"late output\"}", DateTimeOffset.UtcNow), default));
        Assert.True(await service.RecordAsync(new(workflow.Id, "Custom", "first", "stdout", new string('a', 20000), DateTimeOffset.UtcNow), default));
        var loaded = (await store.ListTaskRunsAsync(workflow.Id, default)).Single(run => run.Id == first.Id);
        Assert.Equal("authoritative", loaded.Output); Assert.Equal(status, loaded.Status); Assert.Null(second.Output);
        var logs = await store.ListLogsAsync(workflow.Id, default); Assert.Equal(2, logs.Count);
        Assert.All(logs, log => { Assert.Equal(first.Id, log.TaskRunId); Assert.InRange(log.Message.Length, 1, 16030); });
    }

    [Theory]
    [InlineData("Custom", "")]
    [InlineData("Custom", "unassigned")]
    [InlineData("Custom", "previous-attempt")]
    [InlineData("999", "first")]
    [InlineData("Plan", "first")]
    public async Task Unassigned_stale_and_undefined_kind_callbacks_are_rejected(string kind, string id)
    {
        var (store, workflow, _, _) = await SetupAsync();
        await store.UpsertTaskRunAsync(new() { WorkflowId = workflow.Id, Kind = TaskRunKind.Custom, DefinitionStepId = "unassigned" }, default);
        Assert.False(await new WorkerAgentMessageService(store).RecordAsync(new(workflow.Id, kind, id, "stdout", "hello", DateTimeOffset.UtcNow), default));
        Assert.Empty(await store.ListLogsAsync(workflow.Id, default));
    }

    [Fact]
    public async Task Concurrent_late_streams_cannot_overwrite_authoritative_completion()
    {
        var (store, workflow, first, _) = await SetupAsync(); var service = new WorkerAgentMessageService(store);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(index => (Task)service.RecordAsync(new(workflow.Id, "Custom", "first", "stdout",
            "{\"type\":\"agent_message\",\"message\":\"stream\"}", DateTimeOffset.UtcNow), default)).Append(
            Task.Run(async () => { first.Status = TaskRunStatus.Succeeded; first.Output = "final"; await store.UpsertTaskRunAsync(first, default); })));
        var run = (await store.ListTaskRunsAsync(workflow.Id, default)).Single(item => item.Id == first.Id);
        Assert.Equal("final", run.Output); Assert.Equal(TaskRunStatus.Succeeded, run.Status);
    }

    [Theory]
    [InlineData("first", true)]
    [InlineData("second", true)]
    [InlineData("previous-attempt", false)]
    public async Task Auth_refresh_matches_exact_assigned_custom_execution_not_latest_kind(string externalId, bool expected)
    {
        var (store, workflow, _, _) = await SetupAsync();
        var settings = new AiSettingsService(new InMemoryAiSettingsStore(), Options.Create(new OpenHandsOptions()), new SystemClock());
        await settings.UpdateAsync(new(AuthMethod: OpenHandsAuthMethods.CodexSubscription, CodexAuthJson: "{\"tokens\":\"old\"}",
            Id: "test-ai", Name: "Test"), default);
        Assert.Equal(expected, await new WorkerAgentAuthRefreshService(store, settings).RecordAsync(new(workflow.Id, "Custom", externalId,
            "test-ai", "{\"tokens\":\"refreshed\"}"), default));
        Assert.Equal(expected ? "{\"tokens\":\"refreshed\"}" : "{\"tokens\":\"old\"}", (await settings.ResolveAsync(default)).CodexAuthJson);
    }

    private static async Task<(InMemoryWorkflowStore, Workflow, TaskRun, TaskRun)> SetupAsync()
    {
        var store = new InMemoryWorkflowStore(); var workflow = await store.CreateWorkflowAsync(new() { IssueUrl = "https://example.test/1", RepositoryUrl = "https://example.test/repo" }, default);
        var first = new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.Custom, DefinitionStepId = "a", Status = TaskRunStatus.Running, ExternalId = "first" };
        var second = new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.Custom, DefinitionStepId = "b", Status = TaskRunStatus.Running, ExternalId = "second", CreatedAt = DateTimeOffset.UtcNow.AddSeconds(1) };
        await store.UpsertTaskRunAsync(first, default); await store.UpsertTaskRunAsync(second, default);
        return (store, workflow, first, second);
    }
}
