using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace hhnl.Formicae.Tests;

public sealed class EnvironmentCatalogTests
{
    [Fact]
    public async Task Virtual_default_remains_immutable_and_cannot_be_shadowed_by_stored_row()
    {
        var store = new InMemoryEnvironmentStore(); var service = new EnvironmentService(store);
        var first = Assert.Single(await service.ListAsync(default));
        Assert.True(first.BuiltIn); Assert.Equal("default", first.Id); Assert.Null(first.Configuration.Runtime);
        Assert.Empty(await store.ListAsync(default));
        await store.CreateAsync(new() { Id = "default", Name = "Shadow" }, default);
        Assert.Equal("Default environment", Assert.Single(await service.ListAsync(default)).Name);
        Assert.Equal(first, await service.GetAsync("default", default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync("default", new(1, "changed"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteAsync("default", 1, default));
    }

    [Fact]
    public async Task Revisions_conflicts_and_soft_deletion_preserve_existing_responses()
    {
        var service = new EnvironmentService(new InMemoryEnvironmentStore());
        var first = await service.CreateAsync(new(" Short ", " Description ", new() { Runtime = new(30) }), default);
        Assert.Equal("Short", first.Name); Assert.Equal("Description", first.Description); Assert.False(first.BuiltIn);
        var second = (await service.UpdateAsync(first.Id, new(1, "Longer", Configuration: new() { Runtime = new(50) }), default))!;
        Assert.Equal(2, second.Revision); Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Equal(30, first.Configuration.Runtime!.TimeoutLimitSeconds);
        Assert.Equal(50, (await service.GetAsync(first.Id, default))!.Configuration.Runtime!.TimeoutLimitSeconds);
        await Assert.ThrowsAsync<EnvironmentConflictException>(() => service.UpdateAsync(first.Id, new(1, "old"), default));
        await Assert.ThrowsAsync<EnvironmentConflictException>(() => service.UpdateAsync(first.Id, new(3, "future"), default));
        await Assert.ThrowsAsync<EnvironmentConflictException>(() => service.DeleteAsync(first.Id, 1, default));
        Assert.True(await service.DeleteAsync(first.Id, 2, default));
        Assert.Null(await service.GetAsync(first.Id, default)); Assert.True(Assert.Single(await service.ListAsync(default)).BuiltIn);
        Assert.False(await service.DeleteAsync(first.Id, 3, default)); Assert.Null(await service.UpdateAsync(first.Id, new(3, "gone"), default));
    }

    [Theory]
    [InlineData("empty-name")]
    [InlineData("long-name")]
    [InlineData("long-description")]
    [InlineData("schema")]
    [InlineData("zero-cap")]
    [InlineData("large-cap")]
    [InlineData("image")]
    [InlineData("tools")]
    [InlineData("mcp")]
    [InlineData("null-tools")]
    [InlineData("null-mcp")]
    public async Task Invalid_or_unimplemented_configuration_cannot_be_persisted(string invalid)
    {
        var configuration = invalid switch
        {
            "schema" => new EnvironmentConfiguration { SchemaVersion = 2 },
            "zero-cap" => new() { Runtime = new(0) }, "large-cap" => new() { Runtime = new(3601) },
            "image" => new() { Image = JsonSerializer.SerializeToElement("worker:latest") },
            "tools" => new() { Tools = [JsonSerializer.SerializeToElement("curl")] },
            "mcp" => new() { McpServers = [JsonSerializer.SerializeToElement(new { name = "server" })] },
            "null-tools" => new() { Tools = null! }, "null-mcp" => new() { McpServers = null! }, _ => new()
        };
        var request = new CreateEnvironmentRequest(invalid == "empty-name" ? " " : invalid == "long-name" ? new('x', 121) : "Name",
            invalid == "long-description" ? new('x', 2001) : "", configuration);
        var store = new InMemoryEnvironmentStore();
        await Assert.ThrowsAsync<ArgumentException>(() => new EnvironmentService(store).CreateAsync(request, default));
        Assert.Empty(await store.ListAsync(default));
    }

    [Fact]
    public async Task Failed_validation_does_not_advance_revision_and_empty_configuration_inherits()
    {
        var service = new EnvironmentService(new InMemoryEnvironmentStore());
        var first = await service.CreateAsync(new("Default-like"), default);
        Assert.Null(first.Configuration.Runtime); Assert.Empty(first.Configuration.Tools);
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(first.Id, new(1, "invalid", Configuration: new() { Runtime = new(-1) }), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteAsync(first.Id, 0, default));
        Assert.Equal(1, (await service.GetAsync(first.Id, default))!.Revision);
    }
}

public sealed class EnvironmentPersistenceTests(MigrationPostgresFixture fixture) : IClassFixture<MigrationPostgresFixture>
{
    [Fact]
    public async Task Environment_catalog_persists_configuration_with_atomic_revision_conflicts()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await db.Database.MigrateAsync();
        var service = new EnvironmentService(new EfEnvironmentStore(db));
        var first = await service.CreateAsync(new("Short", Configuration: new() { Runtime = new(15) }), default);
        async Task<EnvironmentResponse?> Update(int cap)
        {
            await using var other = new FormicaeDbContext(new DbContextOptionsBuilder<FormicaeDbContext>().UseNpgsql(db.Database.GetConnectionString()).Options);
            try { return await new EnvironmentService(new EfEnvironmentStore(other)).UpdateAsync(first.Id, new(1, "Short", Configuration: new() { Runtime = new(cap) }), default); }
            catch (EnvironmentConflictException) { return null; }
        }
        var outcomes = await Task.WhenAll(Update(20), Update(30));
        var winner = Assert.Single(outcomes, item => item is not null)!;
        Assert.Equal(winner.Configuration.Runtime, (await service.GetAsync(first.Id, default))!.Configuration.Runtime);
        Assert.True(await service.DeleteAsync(first.Id, 2, default)); Assert.Null(await service.GetAsync(first.Id, default));
        db.ChangeTracker.Clear(); var deleted = await db.ExecutionEnvironments.SingleAsync(); Assert.True(deleted.IsDeleted); Assert.Equal(3, deleted.Revision);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Upgrade_from_custom_tasks_preserves_pinned_documents_and_run_preparation()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await db.GetService<IMigrator>().MigrateAsync("20260906172157_AddCustomTasks");
        var workflow = new Workflow { IssueUrl = "https://github.com/example/repo/issues/15", RepositoryUrl = "https://github.com/example/repo" };
        var definition = new WorkflowDefinition { Name = "Pinned" };
        var custom = new CustomTaskSnapshot("task", 1, "Task", "", "Prompt", [], new());
        var document = new WorkflowDefinitionDocument("formicae.workflow/v1alpha3", "custom",
            [new("custom", CustomTaskDefinitions.Uses, CustomTask: new("task", Snapshot: custom), PersonaSnapshot: PersonaService.DefaultSnapshot)]);
        var version = new WorkflowDefinitionVersion { WorkflowDefinitionId = definition.Id, Version = 1,
            DslSchemaVersion = document.Schema, DefinitionJson = WorkflowDefinitionJson.Serialize(document) };
        var prepared = JsonSerializer.Serialize(new PreparedCustomTaskExecution("task", 1, "Task", new Dictionary<string, JsonElement>(), new Dictionary<string, JsonElement>(), 1800, "Prompt"));
        var run = new TaskRun { WorkflowId = workflow.Id, DefinitionStepId = "custom", Kind = TaskRunKind.Custom,
            Status = TaskRunStatus.Running, ExecutionAttemptId = Guid.NewGuid(), CustomTaskExecutionJson = prepared };
        db.Workflows.Add(workflow); db.WorkflowDefinitions.Add(definition); db.WorkflowDefinitionVersions.Add(version); db.TaskRuns.Add(run);
        await db.SaveChangesAsync(); await db.Database.MigrateAsync(); db.ChangeTracker.Clear();
        Assert.Equal(version.DefinitionJson, (await db.WorkflowDefinitionVersions.SingleAsync()).DefinitionJson);
        var restored = await db.TaskRuns.SingleAsync(); Assert.Equal(prepared, restored.CustomTaskExecutionJson); Assert.Equal(run.ExecutionAttemptId, restored.ExecutionAttemptId);
        Assert.Empty(await db.ExecutionEnvironments.ToListAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }
}
