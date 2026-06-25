using hhnl.Formicae.Application.Workflows;
using Microsoft.EntityFrameworkCore;

namespace hhnl.Formicae.Infrastructure.Persistence;

public sealed class FormicaeDbContext(DbContextOptions<FormicaeDbContext> options) : DbContext(options)
{
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<TaskRun> TaskRuns => Set<TaskRun>();
    public DbSet<WorkflowLog> WorkflowLogs => Set<WorkflowLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Workflow>(entity =>
        {
            entity.ToTable("workflows");
            entity.HasKey(workflow => workflow.Id);
            entity.HasIndex(workflow => workflow.IssueUrl).IsUnique();
            entity.Property(workflow => workflow.IssueUrl).IsRequired();
            entity.Property(workflow => workflow.RepositoryUrl).IsRequired();
            entity.Property(workflow => workflow.BaseBranch).IsRequired();
            entity.Property(workflow => workflow.Status).HasConversion<string>();
            entity.Property(workflow => workflow.CurrentStep).HasConversion<string>();
        });

        modelBuilder.Entity<TaskRun>(entity =>
        {
            entity.ToTable("task_runs");
            entity.HasKey(run => run.Id);
            entity.Property(run => run.Kind).HasConversion<string>();
            entity.Property(run => run.Status).HasConversion<string>();
            entity.HasIndex(run => new { run.WorkflowId, run.Kind }).IsUnique();
        });

        modelBuilder.Entity<WorkflowLog>(entity =>
        {
            entity.ToTable("workflow_logs");
            entity.HasKey(log => log.Id);
            entity.Property(log => log.Message).IsRequired();
            entity.Property(log => log.Level).IsRequired();
            entity.HasIndex(log => log.WorkflowId);
        });
    }
}
