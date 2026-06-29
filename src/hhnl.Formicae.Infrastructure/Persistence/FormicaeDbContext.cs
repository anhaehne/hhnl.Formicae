using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Management;
using hhnl.Formicae.Application.Workflows;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace hhnl.Formicae.Infrastructure.Persistence;

public sealed class FormicaeDbContext(DbContextOptions<FormicaeDbContext> options) : IdentityDbContext<FormicaeUser>(options)
{
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<TaskRun> TaskRuns => Set<TaskRun>();
    public DbSet<WorkflowEvent> WorkflowEvents => Set<WorkflowEvent>();
    public DbSet<WorkflowLog> WorkflowLogs => Set<WorkflowLog>();
    public DbSet<AiSettings> AiSettings => Set<AiSettings>();
    public DbSet<DevOpsIntegration> DevOpsIntegrations => Set<DevOpsIntegration>();
    public DbSet<ConnectedRepository> ConnectedRepositories => Set<ConnectedRepository>();
    public DbSet<InviteCode> InviteCodes => Set<InviteCode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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
            entity.Property(settings => settings.Name).IsRequired().HasDefaultValue(hhnl.Formicae.Application.Workflows.AiSettings.DefaultName);
            entity.Property(settings => settings.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(settings => settings.AgentKind).IsRequired();
            entity.Property(settings => settings.AuthMethod).IsRequired();
        });

        modelBuilder.Entity<DevOpsIntegration>(entity =>
        {
            entity.ToTable("devops_integrations");
            entity.HasKey(integration => integration.Id);
            entity.Property(integration => integration.ProviderType).HasConversion<string>();
            entity.Property(integration => integration.DisplayName).IsRequired();
            entity.Property(integration => integration.GitHubAppClientId).IsRequired();
            entity.Property(integration => integration.GitHubAppPrivateKey);
            entity.Property(integration => integration.WebhookSecret).IsRequired();
            entity.Property(integration => integration.WebhookUrl).IsRequired();
            entity.HasMany(integration => integration.Repositories)
                .WithOne(repository => repository.DevOpsIntegration)
                .HasForeignKey(repository => repository.DevOpsIntegrationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConnectedRepository>(entity =>
        {
            entity.ToTable("connected_repositories");
            entity.HasKey(repository => repository.Id);
            entity.Property(repository => repository.Owner).IsRequired();
            entity.Property(repository => repository.Name).IsRequired();
            entity.Property(repository => repository.RepositoryUrl).IsRequired();
            entity.Property(repository => repository.DefaultBranch).IsRequired();
            entity.HasIndex(repository => new { repository.DevOpsIntegrationId, repository.RepositoryUrl }).IsUnique();
        });

        modelBuilder.Entity<InviteCode>(entity =>
        {
            entity.ToTable("invite_codes");
            entity.HasKey(invite => invite.Id);
            entity.Property(invite => invite.CodeHash).IsRequired();
            entity.Property(invite => invite.CreatedByUserId).IsRequired();
            entity.HasIndex(invite => invite.CodeHash).IsUnique();
        });
    }
}
