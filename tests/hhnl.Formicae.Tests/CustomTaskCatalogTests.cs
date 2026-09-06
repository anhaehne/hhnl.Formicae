using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace hhnl.Formicae.Tests;

public sealed class CustomTaskCatalogTests
{
    [Fact]
    public async Task Catalog_revisions_reject_stale_edits_and_deletes_without_changing_saved_inputs()
    {
        var service = new CustomTaskService(new InMemoryCustomTaskStore());
        Assert.Empty(await service.ListAsync(default));
        using var json = JsonDocument.Parse("42");
        var inputs = new List<CustomTaskInputDefinition> { new("count", "number", true, json.RootElement) };
        var first = await service.CreateAsync(new(" Reviewer ", " Count {{input.count}} \n", " Details ", inputs), default);
        inputs.Clear(); json.Dispose();
        var reread = (await service.GetAsync(first.Id, default))!;
        Assert.Equal("Reviewer", first.Name); Assert.Equal("Details", first.Description);
        Assert.Equal(" Count {{input.count}} \n", reread.PromptTemplate);
        Assert.Equal(42, Assert.Single(reread.Inputs).DefaultValue!.Value.GetInt32());
        Assert.Equal(new CustomTaskRunnerSettings(), reread.Runner);
        var second = (await service.UpdateAsync(first.Id, new(1, "Updated", "New prompt", Runner: new("agent", 17)), default))!;
        Assert.Equal(2, second.Revision); Assert.Equal(first.CreatedAt, second.CreatedAt); Assert.Equal(17, second.Runner.TimeoutSeconds);
        await Assert.ThrowsAsync<CustomTaskConflictException>(() => service.UpdateAsync(first.Id, new(1, "stale", "old"), default));
        await Assert.ThrowsAsync<CustomTaskConflictException>(() => service.UpdateAsync(first.Id, new(3, "future", "old"), default));
        await Assert.ThrowsAsync<CustomTaskConflictException>(() => service.DeleteAsync(first.Id, 1, default));
        Assert.True(await service.DeleteAsync(first.Id, 2, default));
        Assert.Null(await service.GetAsync(first.Id, default)); Assert.Empty(await service.ListAsync(default));
        Assert.False(await service.DeleteAsync(first.Id, 3, default));
        Assert.Null(await service.UpdateAsync(first.Id, new(3, "gone", "prompt"), default));
    }

    [Theory]
    [InlineData("empty-name")]
    [InlineData("long-name")]
    [InlineData("long-description")]
    [InlineData("empty-prompt")]
    [InlineData("long-prompt")]
    [InlineData("unknown-token")]
    [InlineData("unknown-runner")]
    [InlineData("short-timeout")]
    [InlineData("long-timeout")]
    [InlineData("null-input")]
    [InlineData("wrong-default")]
    public async Task Invalid_catalog_configuration_never_persists(string field)
    {
        var request = new CreateCustomTaskRequest(
            field == "empty-name" ? " " : field == "long-name" ? new('n', 121) : "Name",
            field == "empty-prompt" ? " " : field == "long-prompt" ? new('p', 16001) : field == "unknown-token" ? "{{unknown}}" : "Prompt",
            field == "long-description" ? new('d', 2001) : "",
            field == "null-input" ? [null!] : field == "wrong-default" ? [new("x", "boolean", DefaultValue: JsonSerializer.SerializeToElement("yes"))] : [],
            field == "unknown-runner" ? new("script") : field == "short-timeout" ? new("agent", 0) : field == "long-timeout" ? new("agent", 3601) : null);
        var store = new InMemoryCustomTaskStore();
        await Assert.ThrowsAsync<ArgumentException>(() => new CustomTaskService(store).CreateAsync(request, default));
        Assert.Empty(await store.ListAsync(default));
    }

    [Fact]
    public async Task Invalid_update_preserves_current_revision_and_duplicate_names_have_distinct_ids()
    {
        var service = new CustomTaskService(new InMemoryCustomTaskStore());
        var first = await service.CreateAsync(new("Same", "Prompt"), default);
        var second = await service.CreateAsync(new("Same", "Prompt"), default);
        Assert.NotEqual(first.Id, second.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(first.Id, new(1, "changed", "{{bad}}"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteAsync(first.Id, 0, default));
        Assert.Equal(1, (await service.GetAsync(first.Id, default))!.Revision);
        Assert.Equal(2, (await service.ListAsync(default)).Count);
    }
}

public sealed class CustomTaskPersistenceTests(MigrationPostgresFixture fixture) : IClassFixture<MigrationPostgresFixture>
{
    [Fact]
    public async Task Upgrade_from_personas_preserves_prior_execution_and_definition_snapshots()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await db.GetService<IMigrator>().MigrateAsync("20260906164856_AddPersonas");
        var workflow = new Workflow { IssueUrl = "https://github.com/example/repo/issues/14", RepositoryUrl = "https://github.com/example/repo" };
        var definition = new WorkflowDefinition { Name = "Pinned" };
        var document = new WorkflowDefinitionDocument("formicae.workflow/v1alpha3", "plan",
            [new("plan", "builtins.plan", PersonaSnapshot: PersonaService.DefaultSnapshot)]);
        var version = new WorkflowDefinitionVersion { WorkflowDefinitionId = definition.Id, Version = 1,
            DslSchemaVersion = document.Schema, DefinitionJson = WorkflowDefinitionJson.Serialize(document) };
        var parallel = new WorkflowParallelExecution { WorkflowId = workflow.Id, NodeId = "parallel", EntryPlanArtifact = "entry" };
        var decision = new WorkflowDecisionExecution { WorkflowId = workflow.Id, NodeId = "decision", ConfiguredTargetId = "plan",
            SelectedTargetId = "plan", InputJson = "{\"value\":true}", BooleanResult = true };
        db.Workflows.Add(workflow); db.WorkflowDefinitions.Add(definition); db.WorkflowDefinitionVersions.Add(version);
        db.WorkflowParallelExecutions.Add(parallel); db.WorkflowDecisionExecutions.Add(decision);
        await db.SaveChangesAsync();
        var runId = Guid.NewGuid(); var attemptId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO task_runs ("Id", "WorkflowId", "Kind", "DefinitionStepId", "Status", "ExecutionAttemptId", "CreatedAt", "UpdatedAt", "Output")
            VALUES ({runId}, {workflow.Id}, 'Plan', 'plan', 'Succeeded', {attemptId}, {now}, {now}, 'prior result')
            """);
        await db.Database.MigrateAsync(); db.ChangeTracker.Clear();
        Assert.Equal(version.DefinitionJson, (await db.WorkflowDefinitionVersions.SingleAsync()).DefinitionJson);
        Assert.Equal("entry", (await db.WorkflowParallelExecutions.SingleAsync()).EntryPlanArtifact);
        Assert.Equal(decision.InputJson, (await db.WorkflowDecisionExecutions.SingleAsync()).InputJson);
        var run = await db.TaskRuns.SingleAsync();
        Assert.Equal(attemptId, run.ExecutionAttemptId); Assert.Equal("prior result", run.Output); Assert.Null(run.CustomTaskExecutionJson);
        Assert.Empty(await db.CustomTasks.ToListAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Catalog_and_prepared_execution_persist_with_atomic_revision_conflicts()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await db.Database.MigrateAsync();
        var service = new CustomTaskService(new EfCustomTaskStore(db));
        var first = await service.CreateAsync(new("Audit", "Inspect {{input.text}}", Inputs: [new("text", "string", true)], Runner: new("agent", 30)), default);
        async Task<CustomTaskResponse?> Update(string name)
        {
            await using var other = new FormicaeDbContext(new DbContextOptionsBuilder<FormicaeDbContext>().UseNpgsql(db.Database.GetConnectionString()).Options);
            try { return await new CustomTaskService(new EfCustomTaskStore(other)).UpdateAsync(first.Id, new(1, name, "Prompt"), default); }
            catch (CustomTaskConflictException) { return null; }
        }
        Assert.Single(await Task.WhenAll(Update("A"), Update("B")), item => item is not null);
        var winner = (await service.GetAsync(first.Id, default))!; Assert.Equal(2, winner.Revision);
        Assert.True(await service.DeleteAsync(first.Id, 2, default)); Assert.Null(await service.GetAsync(first.Id, default));
        var workflow = new Workflow { IssueUrl = "https://github.com/example/repo/issues/15", RepositoryUrl = "https://github.com/example/repo", Status = WorkflowStatus.Running, CurrentStep = WorkflowStep.Custom };
        var prepared = new PreparedCustomTaskExecution(first.Id, 1, first.Name, new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("review me") }, new Dictionary<string, JsonElement>(), 30, "Inspect review me");
        var payload = JsonSerializer.Serialize(prepared, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var run = new TaskRun { WorkflowId = workflow.Id, DefinitionStepId = "custom", Kind = TaskRunKind.Custom,
            Status = TaskRunStatus.Running, ExecutionAttemptId = Guid.NewGuid(), CustomTaskExecutionJson = payload };
        var workflowStore = new EfWorkflowStore(db);
        await workflowStore.CreateWorkflowAsync(workflow, default); await workflowStore.UpsertTaskRunAsync(run, default); db.ChangeTracker.Clear();
        Assert.Contains(await workflowStore.ListRunnableWorkflowsAsync(default), item => item.Id == workflow.Id);
        var restored = await db.TaskRuns.SingleAsync(task => task.Id == run.Id);
        Assert.Equal(payload, restored.CustomTaskExecutionJson); Assert.Equal(run.ExecutionAttemptId, restored.ExecutionAttemptId);
        Assert.Equal(WorkflowStatus.Running, (await db.Workflows.SingleAsync(item => item.Id == workflow.Id)).Status);
        Assert.True((await db.CustomTasks.SingleAsync()).IsDeleted);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
