using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowConcurrencyPersistenceTests(MigrationPostgresFixture fixture) : IClassFixture<MigrationPostgresFixture>
{
    [Fact]
    public async Task Migration_and_restart_preserve_limits_queue_order_and_active_reservations()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await db.Database.MigrateAsync();
        var store = new EfWorkflowStore(db);
        var definition = await store.CreateWorkflowDefinitionAsync(new() { Name = "Limited" }, default);
        await store.SetDefinitionConcurrencyAsync(definition.Id, 2, default);
        await store.SetConcurrencySettingsAsync(new() { MaxActiveInstances = 3 }, default);
        var queued = await store.CreateWorkflowAsync(new() { IssueUrl = "https://example.test/1", RepositoryUrl = "https://example.test/repo",
            WorkflowDefinitionId = definition.Id, EnqueuedAt = DateTimeOffset.Parse("2026-10-10T20:00:00Z") }, default);
        var active = await store.CreateWorkflowAsync(new() { IssueUrl = "https://example.test/2", RepositoryUrl = "https://example.test/repo",
            WorkflowDefinitionId = definition.Id, HasExecutionSlot = true }, default);
        var options = new DbContextOptionsBuilder<FormicaeDbContext>().UseNpgsql(db.Database.GetConnectionString()!).Options;
        await using var restarted = new FormicaeDbContext(options); var recovered = new EfWorkflowStore(restarted);
        Assert.Equal(3, (await recovered.GetConcurrencySettingsAsync(default)).MaxActiveInstances);
        Assert.Equal(2, (await recovered.GetWorkflowDefinitionAsync(definition.Id, default))!.MaxActiveInstances);
        Assert.Equal(queued.EnqueuedAt, (await recovered.GetWorkflowAsync(queued.Id, default))!.EnqueuedAt);
        Assert.True((await recovered.GetWorkflowAsync(active.Id, default))!.HasExecutionSlot);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
