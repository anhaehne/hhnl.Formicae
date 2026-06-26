using hhnl.Formicae.Application.Auth;
using hhnl.Formicae.Application.Workflows;
using Microsoft.EntityFrameworkCore;

namespace hhnl.Formicae.Infrastructure.Persistence;

public sealed class FormicaeDbContext(DbContextOptions<FormicaeDbContext> options) : DbContext(options)
{
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<TaskRun> TaskRuns => Set<TaskRun>();
    public DbSet<WorkflowEvent> WorkflowEvents => Set<WorkflowEvent>();
    public DbSet<WorkflowLog> WorkflowLogs => Set<WorkflowLog>();
    public DbSet<AiSettings> AiSettings => Set<AiSettings>();
    public DbSet<AuthUser> AuthUsers => Set<AuthUser>();

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

        modelBuilder.Entity<WorkflowEvent>(entity =>
        {
            entity.ToTable("workflow_events");
            entity.HasKey(evt => evt.Id);
            entity.Property(evt => evt.Type).IsRequired();
            entity.Property(evt => evt.Level).IsRequired();
            entity.Property(evt => evt.Message).IsRequired();
            entity.HasIndex(evt => evt.WorkflowId);
            entity.HasIndex(evt => new { evt.WorkflowId, evt.CreatedAt });
        });

        modelBuilder.Entity<WorkflowLog>(entity =>
        {
            entity.ToTable("workflow_logs");
            entity.HasKey(log => log.Id);
            entity.Property(log => log.Message).IsRequired();
            entity.Property(log => log.Level).IsRequired();
            entity.HasIndex(log => log.WorkflowId);
        });

        modelBuilder.Entity<AiSettings>(entity =>
        {
            entity.ToTable("ai_settings");
            entity.HasKey(settings => settings.Id);
            entity.Property(settings => settings.Id).IsRequired();
            entity.Property(settings => settings.AuthMethod).IsRequired();
        });

        modelBuilder.Entity<AuthUser>(entity =>
        {
            entity.ToTable("auth_users");
            entity.HasKey(user => user.Id);
            entity.HasIndex(user => user.GitHubUserId).IsUnique();
            entity.HasIndex(user => user.GitHubLogin);
            entity.Property(user => user.GitHubUserId).IsRequired();
            entity.Property(user => user.GitHubLogin).IsRequired();
        });
    }
}
