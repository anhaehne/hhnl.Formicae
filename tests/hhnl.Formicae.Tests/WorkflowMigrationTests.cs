using System.Net.Http.Json;
using System.Text.Json;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace hhnl.Formicae.Tests;

public sealed class MigrationPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    public async Task<FormicaeDbContext> CreateDatabaseAsync()
    {
        var name = "migration_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = name }.ToString();
        return new FormicaeDbContext(new DbContextOptionsBuilder<FormicaeDbContext>().UseNpgsql(connectionString).Options);
    }
}

public sealed class WorkflowMigrationTests(MigrationPostgresFixture fixture) : IClassFixture<MigrationPostgresFixture>
{
    private const string PreLoopMigration = "20260709152649_AddWorkflowTriggerEvents";
    private static readonly Guid WorkflowId = Guid.Parse("74000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task EmptyDatabase_MigratesToLatest()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Empty(await db.TaskRuns.ToListAsync());
        Assert.Empty(await db.WorkflowLoopIterations.ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyHistory_IsPreserved_AndApiStarts(bool customDefinition)
    {
        await using var db = await CreateLegacyDatabaseAsync();
        if (customDefinition)
        {
            await SetDefinitionAsync(db, CustomSteps);
        }
        var before = await HistoryAsync(db);
        await db.Database.MigrateAsync();
        Assert.Equal(before, await HistoryAsync(db));
        var runs = await db.TaskRuns.AsNoTracking().OrderBy(run => run.Id).ToListAsync();
        Assert.Equal(4, runs.Count);
        Assert.All(runs, run => Assert.Null(run.LoopIteration));
        Assert.All(runs, run => Assert.Null(run.ExecutionAttemptId));
        Assert.Empty(await db.WorkflowParallelExecutions.AsNoTracking().ToListAsync());
        Assert.Equal(customDefinition ? ["draft", "code", "pr", "review"] :
            new[] { "plan", "implement", "createPullRequest", "addressComments" }, runs.Select(run => run.DefinitionStepId));
        var workflow = await db.Workflows.SingleAsync();
        Assert.Equal(customDefinition ? "review" : "addressComments", workflow.CurrentDefinitionStepId);

        // Startup runs MigrateAsync again against the upgraded database and queries through the API/store.
        await using var factory = new MigrationApiFactory(db.Database.GetConnectionString()!);
        using var http = factory.CreateClient();
        (await http.GetAsync("/healthz")).EnsureSuccessStatusCode();
        (await http.GetAsync($"/api/workflows/{WorkflowId}")).EnsureSuccessStatusCode();
        var apiRuns = await http.GetFromJsonAsync<JsonElement[]>($"/api/workflows/{WorkflowId}/runs");
        Assert.Equal(runs.Select(run => run.Id).Order(), apiRuns!.Select(run => run.GetProperty("id").GetGuid()).Order());
        Assert.Equal(before, await HistoryAsync(db));

        // NULLS NOT DISTINCT must reject a second non-loop run, while different iterations are valid.
        var stepId = runs[0].DefinitionStepId;
        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertRunAsync(db, stepId, null));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("IX_task_runs_WorkflowId_DefinitionStepId_LoopIteration", error.ConstraintName);
        await InsertRunAsync(db, stepId, 1);
        await InsertRunAsync(db, stepId, 2);
        error = await Assert.ThrowsAsync<PostgresException>(() => InsertRunAsync(db, stepId, 1));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    [InlineData("duplicate")]
    [InlineData("blank")]
    [InlineData("unknown-kind")]
    [InlineData("missing-version")]
    [InlineData("orphan")]
    public async Task UnmappableHistory_AbortsWithoutChangingRows(string scenario)
    {
        await using var db = await CreateLegacyDatabaseAsync();
        var steps = scenario switch
        {
            "missing" => CustomSteps.Replace("builtins.plan", "builtins.unknown"),
            "ambiguous" => CustomSteps.Replace("builtins.implement", "builtins.plan"),
            "duplicate" => CustomSteps.Replace("\"code\"", "\"draft\""),
            "blank" => CustomSteps.Replace("\"code\"", "\" \""),
            _ => CustomSteps
        };
        await SetDefinitionAsync(db, steps);
        if (scenario == "unknown-kind")
            await db.Database.ExecuteSqlRawAsync("UPDATE task_runs SET \"Kind\" = 'Unknown' WHERE \"Kind\" = 'Plan'");
        if (scenario == "missing-version")
            await db.Database.ExecuteSqlRawAsync("DELETE FROM workflow_definition_versions");
        if (scenario == "orphan")
            await db.Database.ExecuteSqlRawAsync("DELETE FROM workflows");
        var before = await HistoryAsync(db);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
        Assert.Contains("Cannot normalize legacy workflow", error.MessageText);
        Assert.Equal(before, await HistoryAsync(db));
        Assert.Equal(PreLoopMigration, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'task_runs' AND column_name = 'DefinitionStepId') AS \"Value\"").SingleAsync());
    }

    [Theory]
    [InlineData("Plan", "draft")]
    [InlineData("Implement", "code")]
    [InlineData("CreatePullRequest", "pr")]
    [InlineData("AddressComments", "review")]
    [InlineData("None", null)]
    [InlineData("Done", null)]
    public async Task CursorWithoutTaskRun_IsBackfilledOnlyForExecutableSteps(string currentStep, string? expected)
    {
        await using var db = await CreateLegacyDatabaseAsync();
        await SetDefinitionAsync(db, CustomSteps);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM task_runs");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflows SET \"CurrentStep\" = {currentStep}");
        await db.Database.MigrateAsync();
        Assert.Equal(expected, (await db.Workflows.SingleAsync()).CurrentDefinitionStepId);
    }

    private async Task<FormicaeDbContext> CreateLegacyDatabaseAsync()
    {
        var db = await fixture.CreateDatabaseAsync();
        await db.GetService<IMigrator>().MigrateAsync(PreLoopMigration);
        await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-0.7.4.sql")));
        return db;
    }

    private const string CustomSteps = """
        [{"id":"draft","uses":"builtins.plan"},
         {"id":"code","uses":"builtins.implement"},
         {"id":"pr","uses":"builtins.create-pull-request"},
         {"id":"review","uses":"builtins.address-comments"}]
        """;

    private static async Task SetDefinitionAsync(FormicaeDbContext db, string steps)
    {
        var definitionId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var json = "{\"schema\":\"formicae.workflow/v1alpha1\",\"startStepId\":\"draft\",\"steps\":" + steps + "}";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO workflow_definitions ("Id", "Name", "CreatedAt", "UpdatedAt")
            VALUES ({definitionId}, 'Custom immutable definition', NOW(), NOW());
            INSERT INTO workflow_definition_versions ("Id", "WorkflowDefinitionId", "Version", "DslSchemaVersion", "IsEnabled", "IsDefault", "DefinitionJson", "CreatedAt")
            VALUES ({versionId}, {definitionId}, 1, 'formicae.workflow/v1alpha1', false, false, {json}, NOW());
            UPDATE workflows SET "WorkflowDefinitionId" = {definitionId}, "WorkflowDefinitionVersionId" = {versionId};
            """);
    }

    private static Task<int> InsertRunAsync(FormicaeDbContext db, string stepId, int? iteration)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO task_runs ("Id", "WorkflowId", "Kind", "Status", "DefinitionStepId", "LoopIteration", "CreatedAt", "UpdatedAt")
            VALUES ({Guid.NewGuid()}, {WorkflowId}, 'Plan', 'Queued', {stepId}, {iteration}, NOW(), NOW())
            """);

    // Compare every legacy column, including IDs, timestamps, retry state, and related history.
    private static Task<string> HistoryAsync(FormicaeDbContext db)
        => db.Database.SqlQueryRaw<string>("""
            SELECT jsonb_build_object(
                'runs', (SELECT jsonb_agg(to_jsonb(r) - 'DefinitionStepId' - 'LoopIteration' - 'ExecutionAttemptId' ORDER BY "Id") FROM task_runs r),
                'workflows', (SELECT jsonb_agg(to_jsonb(w) - 'CurrentDefinitionStepId' ORDER BY "Id") FROM workflows w),
                'logs', (SELECT jsonb_agg(to_jsonb(l) ORDER BY "Id") FROM workflow_logs l),
                'events', (SELECT jsonb_agg(to_jsonb(e) ORDER BY "Id") FROM workflow_events e)
            )::text AS "Value"
            """).SingleAsync();

    private sealed class MigrationApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["UseFakeAdapters"] = "false",
                ["PersistenceMode"] = "Postgres",
                ["ConnectionStrings:Formicae"] = connectionString,
                ["WorkItemMode"] = "Fake",
                ["SourceControlMode"] = "Fake",
                ["AgentMode"] = "Fake",
                ["WorkflowDiscovery:Enabled"] = "false",
                ["ManagementAuth:Enabled"] = "false"
            })
            {
                builder.UseSetting(key, value);
            }
        }
    }
}
