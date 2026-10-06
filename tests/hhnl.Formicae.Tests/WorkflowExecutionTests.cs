using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Tests.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowExecutionTests
{
    [Fact]
    public async Task Logs_page_latest_older_and_replay_are_stable_and_filtered()
    {
        var store = new InMemoryWorkflowStore();
        var workflow = await CreateAsync(store);
        var task = Guid.NewGuid(); var attempt = Guid.NewGuid();
        for (var index = 0; index < 7; index++)
            await store.AddLogAsync(new WorkflowLog { WorkflowId = workflow.Id, TaskRunId = task, ExecutionAttemptId = attempt,
                Source = index == 2 ? "stderr" : "stdout", Message = $"line {index}" }, default);
        var latest = await store.QueryLogsAsync(workflow.Id, new(Limit: 3), default);
        Assert.Equal(new long[] { 5, 6, 7 }, latest.Items.Select(log => log.Sequence));
        Assert.True(latest.HasEarlier); Assert.False(latest.HasMore);
        var older = await store.QueryLogsAsync(workflow.Id, new(Before: latest.PreviousCursor, Limit: 3), default);
        Assert.Equal(new long[] { 2, 3, 4 }, older.Items.Select(log => log.Sequence));
        var replay = await store.QueryLogsAsync(workflow.Id, new(After: 3, Limit: 2), default);
        Assert.Equal(new long[] { 4, 5 }, replay.Items.Select(log => log.Sequence)); Assert.True(replay.HasMore);
        var filtered = await store.QueryLogsAsync(workflow.Id, new(TaskRunId: task, ExecutionAttemptId: attempt, Source: "stderr", Search: "LINE 2"), default);
        Assert.Equal("line 2", Assert.Single(filtered.Items).Message);
        Assert.Empty((await store.QueryLogsAsync(workflow.Id, new(ExecutionAttemptId: Guid.NewGuid()), default)).Items);
    }

    [Fact]
    public async Task Callback_append_is_idempotent_and_rejects_stale_or_completed_attempts()
    {
        var store = new InMemoryWorkflowStore(); var workflow = await CreateAsync(store);
        var attempt = Guid.NewGuid(); var run = new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.Plan,
            Status = TaskRunStatus.Running, ExternalId = "job", ExecutionAttemptId = attempt, Output = "authoritative" };
        await store.UpsertTaskRunAsync(run, default);
        var log = new WorkflowLog { WorkflowId = workflow.Id, TaskRunId = run.Id, ExecutionAttemptId = attempt,
            Source = "stdout", SourceSequence = 1, Message = "live" };
        Assert.True(await store.TryAddWorkerLogAsync(log, "job", attempt, default));
        Assert.True(await store.TryAddWorkerLogAsync(log, "job", attempt, default));
        Assert.Single(await store.ListLogsAsync(workflow.Id, default));
        Assert.False(await store.TryAddWorkerLogAsync(log, "wrong", attempt, default));
        run.ExecutionAttemptId = Guid.NewGuid();
        var stale = new WorkflowLog { WorkflowId = workflow.Id, TaskRunId = run.Id, ExecutionAttemptId = attempt, Message = "stale" };
        Assert.False(await store.TryAddWorkerLogAsync(stale, "job", attempt, default));
        run.Status = TaskRunStatus.Succeeded;
        Assert.False(await store.TryAddWorkerLogAsync(stale, "job", run.ExecutionAttemptId, default));
        Assert.Equal("authoritative", run.Output);
    }

    [Fact]
    public async Task Retry_archives_output_inputs_provenance_and_previous_attempt_identity()
    {
        var store = new InMemoryWorkflowStore(); var workflow = await CreateAsync(store); workflow.Status = WorkflowStatus.Failed;
        var attempt = Guid.NewGuid(); var run = new TaskRun { WorkflowId = workflow.Id, Status = TaskRunStatus.Failed,
            Kind = TaskRunKind.Plan, ExecutionAttemptId = attempt, ExternalId = "old", Output = "old output", FailureReason = "failure",
            StructuredOutputsJson = "{\"value\":1}", CustomTaskExecutionJson = "{\"provenance\":{\"runId\":\"old\"}}" };
        await store.UpsertTaskRunAsync(run, default);
        await new WorkflowService(store).RetryTaskRunAsync(workflow.Id, run.Id, default);
        var archived = Assert.Single(await store.ListTaskRunAttemptsAsync(workflow.Id, default));
        Assert.Equal(attempt, archived.ExecutionAttemptId); Assert.Equal("old output", archived.Output);
        Assert.Equal("old", archived.ExternalId); Assert.Equal("{\"value\":1}", archived.StructuredOutputsJson);
        Assert.Contains("provenance", archived.CustomTaskExecutionJson!); Assert.NotEqual(attempt, run.ExecutionAttemptId);
        Assert.Null(run.Output); Assert.Equal(TaskRunStatus.Queued, run.Status);
        var retryLog = Assert.Single((await store.QueryLogsAsync(workflow.Id,
            new(TaskRunId: run.Id, ExecutionAttemptId: run.ExecutionAttemptId), default)).Items);
        Assert.Equal(run.ExecutionAttemptId, retryLog.ExecutionAttemptId);
        Assert.Contains("queued for retry", retryLog.Message);
    }

    [Fact]
    public async Task Retry_cannot_replace_worker_handle_before_cleanup_or_after_cancel()
    {
        var store = new InMemoryWorkflowStore(); var workflow = await CreateAsync(store);
        var run = new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.Plan, Status = TaskRunStatus.Failed,
            ExternalId = "pending", RuntimeCleanupPending = true };
        await store.UpsertTaskRunAsync(run, default);
        var service = new WorkflowService(store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RetryTaskRunAsync(workflow.Id, run.Id, default));
        Assert.Equal("pending", run.ExternalId); Assert.Empty(await store.ListTaskRunAttemptsAsync(workflow.Id, default));
        run.RuntimeCleanupPending = false; workflow.CancelRequestedAt = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RetryTaskRunAsync(workflow.Id, run.Id, default));
    }

    [Fact]
    public async Task Execution_uses_pinned_definition_and_counts_archived_attempts()
    {
        var store = new InMemoryWorkflowStore();
        var definition = await store.CreateWorkflowDefinitionAsync(new WorkflowDefinition { Name = "Pinned" }, default);
        var version = await store.CreateWorkflowDefinitionVersionAsync(new WorkflowDefinitionVersion { WorkflowDefinitionId = definition.Id,
            Version = 1, DslSchemaVersion = DefaultWorkflowDefinitions.V1Alpha1Schema, DefinitionJson = WorkflowDefinitionJson.Serialize(DefaultWorkflowDefinitions.CreateMvpDocument()) }, default);
        var workflow = await CreateAsync(store); workflow.WorkflowDefinitionVersionId = version.Id;
        var run = new TaskRun { WorkflowId = workflow.Id, Kind = TaskRunKind.Plan, DefinitionStepId = "plan", Status = TaskRunStatus.Failed };
        await store.UpsertTaskRunAsync(run, default); await store.ArchiveTaskRunAttemptAsync(run, default);
        run.ExecutionAttemptId = Guid.NewGuid(); run.Status = TaskRunStatus.Running;
        var response = await Service(store).GetExecutionAsync(workflow.Id, default);
        Assert.Equal(version.Id, response!.DefinitionVersionId); Assert.NotNull(response.Definition);
        Assert.Equal(2, Assert.Single(response.Runs).AttemptCount); Assert.Single(response.Attempts);
    }

    [Fact]
    public async Task Search_filters_status_repository_definition_date_and_pages_newest()
    {
        var store = new InMemoryWorkflowStore(); var first = await CreateAsync(store); var second = await CreateAsync(store);
        first.Status = WorkflowStatus.Failed; first.FailureReason = "specific crash"; var definition = Guid.NewGuid(); first.WorkflowDefinitionId = definition;
        var query = new WorkflowSearchQuery(Search: "SPECIFIC", Status: WorkflowStatus.Failed, RepositoryUrl: first.RepositoryUrl,
            DefinitionId: definition, From: first.CreatedAt.AddMinutes(-1), To: first.CreatedAt.AddMinutes(1), Limit: 1);
        var page = await store.SearchWorkflowsAsync(query, default);
        Assert.Equal(first.Id, Assert.Single(page.Items).WorkflowId); Assert.Equal(1, page.TotalCount);
        Assert.Empty((await store.SearchWorkflowsAsync(query with { Offset = 1 }, default)).Items);
        Assert.Equal(second.Id, Assert.Single((await store.SearchWorkflowsAsync(new(Active: true), default)).Items).WorkflowId);
        Assert.Equal(first.Id, Assert.Single((await store.SearchWorkflowsAsync(new(Search: first.Id.ToString()), default)).Items).WorkflowId);
    }

    [Theory]
    [InlineData("{\"personaRevision\":\"legacy\"}")]
    [InlineData("[]")]
    [InlineData("invalid")]
    public async Task Malformed_legacy_settings_audit_does_not_break_investigation(string json)
    {
        var store = new InMemoryWorkflowStore(); var workflow = await CreateAsync(store);
        await store.AddEventAsync(new WorkflowEvent { WorkflowId = workflow.Id, TaskRunId = Guid.NewGuid(),
            Type = "AgentSettingsResolved", Message = "legacy", DetailsJson = json }, default);
        Assert.NotNull(await Service(store).GetExecutionAsync(workflow.Id, default));
        Assert.NotNull(await Service(store).ExportEvidenceAsync(workflow.Id, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Settings_audit_preserves_attempt_identity_across_camel_and_Pascal_property_names(bool pascal)
    {
        var store = new InMemoryWorkflowStore(); var workflow = await CreateAsync(store);
        var runId = Guid.NewGuid(); var attemptId = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [pascal ? "ExecutionAttemptId" : "executionAttemptId"] = attemptId,
            [pascal ? "AiSettingsId" : "aiSettingsId"] = "configured",
            [pascal ? "Model" : "model"] = "model",
            [pascal ? "PersonaRevision" : "personaRevision"] = 2,
            [pascal ? "Environment" : "environment"] = new { id = "environment" }
        });
        await store.AddEventAsync(new WorkflowEvent { WorkflowId = workflow.Id, TaskRunId = runId,
            Type = "AgentSettingsResolved", Message = "resolved", DetailsJson = json }, default);
        var settings = Assert.Single((await Service(store).GetExecutionAsync(workflow.Id, default))!.ResolvedSettings);
        Assert.Equal(runId, settings.TaskRunId); Assert.Equal(attemptId, settings.ExecutionAttemptId);
        Assert.Equal("configured", settings.AiSettingsId); Assert.Equal("model", settings.Model);
        Assert.Equal(2, settings.PersonaRevision); Assert.Equal("environment", settings.Environment!.Value.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Pause_resume_cancel_are_durable_idempotent_and_terminal_safe()
    {
        var store = new InMemoryWorkflowStore(); var workflow = await CreateAsync(store); var service = Service(store);
        await service.SetControlAsync(workflow.Id, "pause", default); Assert.True(workflow.IsPaused);
        await service.SetControlAsync(workflow.Id, "pause", default); Assert.Single(await store.ListEventsAsync(workflow.Id, default));
        await service.SetControlAsync(workflow.Id, "resume", default); Assert.False(workflow.IsPaused);
        await service.SetControlAsync(workflow.Id, "cancel", default); Assert.NotNull(workflow.CancelRequestedAt);
        await service.SetControlAsync(workflow.Id, "cancel", default); Assert.Equal(3, (await store.ListEventsAsync(workflow.Id, default)).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetControlAsync(workflow.Id, "resume", default));
        workflow.Status = WorkflowStatus.Completed; workflow.CancelRequestedAt = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetControlAsync(workflow.Id, "pause", default));
    }

    [Fact]
    public async Task Scheduler_revisits_terminal_workflows_with_pending_cleanup()
    {
        var store = new InMemoryWorkflowStore(); var workflow = await CreateAsync(store); workflow.Status = WorkflowStatus.Completed;
        var run = new TaskRun { WorkflowId = workflow.Id, RuntimeCleanupPending = true };
        await store.UpsertTaskRunAsync(run, default);
        Assert.Single(await store.ListRunnableWorkflowsAsync(default)); run.RuntimeCleanupPending = false;
        Assert.Empty(await store.ListRunnableWorkflowsAsync(default));
    }

    [Theory]
    [InlineData("execution")]
    [InlineData("logs/page")]
    [InlineData("logs/stream")]
    [InlineData("logs/download")]
    [InlineData("evidence")]
    public async Task Investigation_endpoints_require_workflow_view(string path)
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/workflows/{Guid.NewGuid()}/{path}")).StatusCode);
        var user = await factory.CreateUserAsync("reader");
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateAuthenticatedClient(user.Id).GetAsync($"/api/workflows/{Guid.NewGuid()}/{path}")).StatusCode);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("cancel")]
    public async Task Controls_require_operate_and_refuse_scheduler_lock_contention(string action)
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(true);
        var viewer = await factory.CreateViewerAsync("viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateAuthenticatedClient(viewer.Id).PostAsync($"/api/workflows/{Guid.NewGuid()}/{action}", null)).StatusCode);
        await using var unlocked = new ManagementAuthApiTests.FormicaeApiFactory(false);
        using var scope = unlocked.Services.CreateScope();
        await using var handle = await scope.ServiceProvider.GetRequiredService<IWorkflowOrchestrationLock>().TryAcquireAsync(default);
        Assert.Equal(HttpStatusCode.Conflict, (await unlocked.CreateClient().PostAsync($"/api/workflows/{Guid.NewGuid()}/{action}", null)).StatusCode);
    }

    [Fact]
    public async Task API_pages_and_search_bind_optional_queries_and_validate_cursors()
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(false);
        using var scope = factory.Services.CreateScope(); var store = scope.ServiceProvider.GetRequiredService<IWorkflowStore>();
        var workflow = await CreateAsync(store);
        await store.AddLogAsync(new WorkflowLog { WorkflowId = workflow.Id, Message = "visible" }, default);
        var client = factory.CreateClient();
        Assert.Single((await client.GetFromJsonAsync<WorkflowLogPage>($"/api/workflows/{workflow.Id}/logs/page"))!.Items);
        Assert.Contains((await client.GetFromJsonAsync<WorkflowSearchPage>("/api/workflows/search"))!.Items, item => item.WorkflowId == workflow.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/workflows/{workflow.Id}/logs/page?after=1&before=2")).StatusCode);
    }

    [Fact]
    public async Task SSE_resumes_after_LastEventId_and_rejects_invalid_cursor()
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(false);
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowStore>();
        var workflow = await CreateAsync(store);
        await store.AddLogAsync(new WorkflowLog { WorkflowId = workflow.Id, Message = "first" }, default);
        await store.AddLogAsync(new WorkflowLog { WorkflowId = workflow.Id, Message = "second" }, default);
        using var client = factory.CreateClient();
        using var bad = new HttpRequestMessage(HttpMethod.Get, $"/api/workflows/{workflow.Id}/logs/stream");
        bad.Headers.Add("Last-Event-ID", "invalid");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(bad)).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/workflows/{workflow.Id}/logs/stream?after=0");
        request.Headers.Add("Last-Event-ID", "1");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var lines = new List<string>();
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            lines.Add(line);
            if (line.Length == 0) break;
        }
        Assert.Contains("id: 2", lines);
        Assert.Contains("event: log", lines);
        Assert.Contains(lines, line => line.StartsWith("data: ") && line.Contains("second"));
        Assert.DoesNotContain(lines, line => line.Contains("first"));
    }

    [Fact]
    public async Task Download_is_filtered_bounded_and_redacts_common_credentials()
    {
        var store = new InMemoryWorkflowStore(); var workflow = await CreateAsync(store);
        for (var index = 0; index < 11000; index++)
            await store.AddLogAsync(new WorkflowLog { WorkflowId = workflow.Id, Source = "stderr", Message = $"record {index}" }, default);
        await store.AddLogAsync(new WorkflowLog { WorkflowId = workflow.Id, Source = "stdout", Message = "excluded" }, default);
        var export = await Service(store).DownloadLogsAsync(workflow.Id, new(Source: "stderr"), default);
        Assert.Equal(10000, export.Items.Count); Assert.True(export.HasEarlier);
        Assert.Equal("record 1000", export.Items[0].Message);
        Assert.All(export.Items, log => Assert.Equal("stderr", log.Source));
        var largeWorkflow = await CreateAsync(store);
        for (var index = 0; index < 10; index++)
            await store.AddLogAsync(new WorkflowLog { WorkflowId = largeWorkflow.Id, Message = new string('é', 500000) }, default);
        var byteBounded = await Service(store).DownloadLogsAsync(largeWorkflow.Id, new(), default);
        Assert.True(byteBounded.HasEarlier);
        Assert.True(byteBounded.Items.Sum(log => System.Text.Encoding.UTF8.GetByteCount(log.Message) + 256) <= 8 * 1024 * 1024);
        var evidence = WorkflowEvidenceSanitizer.Sanitize(new
        {
            apiKey = "secret", customTaskExecutionJson = "{\"inputs\":{\"password\":\"private\",\"count\":1}}",
            message = "Authorization: Bearer dangerous api_key=hidden", model = "useful"
        })!.ToJsonString();
        Assert.DoesNotContain("secret", evidence); Assert.DoesNotContain("private", evidence);
        Assert.DoesNotContain("dangerous", evidence); Assert.DoesNotContain("hidden", evidence); Assert.Contains("useful", evidence);
    }

    [Fact]
    public async Task Execution_includes_durable_parallel_outcome_and_timing()
    {
        var store = new InMemoryWorkflowStore();
        var definition = await store.CreateWorkflowDefinitionAsync(new WorkflowDefinition { Name = "Parallel" }, default);
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "group",
            [new("group", WorkflowParallelDefinitions.Uses, Parallel: new(["branch"]))]);
        var version = await store.CreateWorkflowDefinitionVersionAsync(new WorkflowDefinitionVersion { WorkflowDefinitionId = definition.Id,
            Version = 1, DslSchemaVersion = document.Schema, DefinitionJson = WorkflowDefinitionJson.Serialize(document) }, default);
        var workflow = await CreateAsync(store); workflow.WorkflowDefinitionVersionId = version.Id;
        var parallel = new WorkflowParallelExecution { WorkflowId = workflow.Id, NodeId = "group",
            Outcome = WorkflowParallelExecutionOutcome.Succeeded, CompletedAt = DateTimeOffset.UtcNow };
        await store.UpsertParallelExecutionAsync(parallel, default);
        var response = await Service(store).GetExecutionAsync(workflow.Id, default);
        var evidence = Assert.Single(response!.Parallels);
        Assert.Equal(WorkflowParallelExecutionOutcome.Succeeded, evidence.Outcome);
        Assert.Equal(parallel.StartedAt, evidence.StartedAt); Assert.Equal(parallel.CompletedAt, evidence.CompletedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Structural_recovery_cannot_restart_a_canceled_failed_workflow(bool cancellationCompleted)
    {
        var store = new InMemoryWorkflowStore();
        var definition = await store.CreateWorkflowDefinitionAsync(new WorkflowDefinition { Name = "Decision" }, default);
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "decision",
            [new("decision", WorkflowDecisionDefinitions.Uses, Decision: new(new("literal", "boolean", "equals"), "yes", "no"))]);
        var version = await store.CreateWorkflowDefinitionVersionAsync(new WorkflowDefinitionVersion { WorkflowDefinitionId = definition.Id,
            Version = 1, DslSchemaVersion = document.Schema, DefinitionJson = WorkflowDefinitionJson.Serialize(document) }, default);
        var workflow = await CreateAsync(store); workflow.WorkflowDefinitionVersionId = version.Id;
        workflow.Status = WorkflowStatus.Failed; workflow.CurrentDefinitionStepId = "decision";
        workflow.CancelRequestedAt = DateTimeOffset.UtcNow;
        workflow.CancelCompletedAt = cancellationCompleted ? DateTimeOffset.UtcNow : null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowService(store).RetryWorkflowAsync(workflow.Id, default));
        Assert.Equal(WorkflowStatus.Failed, workflow.Status);
        Assert.Empty(await store.ListTaskRunsAsync(workflow.Id, default));
    }

    private static WorkflowExecutionService Service(IWorkflowStore store) => new(store, new SystemClock());
    private static Task<Workflow> CreateAsync(IWorkflowStore store) => store.CreateWorkflowAsync(new Workflow
        { IssueUrl = $"https://example.test/issues/{Guid.NewGuid()}", RepositoryUrl = "https://example.test/repo" }, default);
}
