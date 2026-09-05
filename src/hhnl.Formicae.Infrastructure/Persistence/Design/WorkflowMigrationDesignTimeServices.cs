using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace hhnl.Formicae.Infrastructure.Persistence.Design;

public sealed class WorkflowMigrationDesignTimeServices : IDesignTimeServices
{
    public void ConfigureDesignTimeServices(IServiceCollection services)
        => services.AddSingleton<IMigrationsCodeGenerator, WorkflowMigrationsCodeGenerator>();
}

// Keep the data operation reproducible with dotnet ef; generated migration files remain untouched.
public sealed class WorkflowMigrationsCodeGenerator(
    MigrationsCodeGeneratorDependencies dependencies,
    CSharpMigrationsGeneratorDependencies csharpDependencies)
    : CSharpMigrationsGenerator(dependencies, csharpDependencies)
{
    public override string GenerateMigration(string? migrationNamespace, string migrationName,
        IReadOnlyList<MigrationOperation> upOperations, IReadOnlyList<MigrationOperation> downOperations)
    {
        if (migrationName == "AddWorkflowLoops")
        {
            var operations = upOperations.ToList();
            var index = operations.FindIndex(operation => operation is CreateIndexOperation
            {
                Table: "task_runs", Name: "IX_task_runs_WorkflowId_DefinitionStepId_LoopIteration"
            });
            if (index < 0)
            {
                throw new InvalidOperationException("AddWorkflowLoops must create the task-run loop index after normalization.");
            }

            using var stream = typeof(WorkflowMigrationsCodeGenerator).Assembly.GetManifestResourceStream(
                "hhnl.Formicae.Infrastructure.Persistence.Design.NormalizeLegacyTaskRuns.sql")!;
            using var reader = new StreamReader(stream);
            operations.Insert(index, new SqlOperation { Sql = reader.ReadToEnd() });
            upOperations = operations;
        }

        return base.GenerateMigration(migrationNamespace, migrationName, upOperations, downOperations);
    }
}
