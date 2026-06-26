using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Infrastructure.GitHub;
using hhnl.Formicae.Infrastructure.Identity;
using hhnl.Formicae.Infrastructure.Kubernetes;
using hhnl.Formicae.Infrastructure.OpenHands;
using hhnl.Formicae.Infrastructure.Persistence;
using hhnl.Formicae.Infrastructure.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Octokit;

namespace hhnl.Formicae.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddFormicaeInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<WorkflowService>();
        services.AddScoped<WorkflowOrchestrator>();
        services.AddScoped<WorkflowDiscoveryService>();
        services.AddScoped<WorkflowObservabilityService>();
        services.AddScoped<WorkerAgentMessageService>();
        services.AddScoped<AiSettingsService>();
        services.AddScoped<DevOpsIntegrationService>();
        services.AddScoped<ManagementUserService>();
        services.AddScoped<InviteService>();
        services.AddSingleton<IClock, SystemClock>();
        services.Configure<ManagementAuthOptions>(configuration.GetSection("ManagementAuth"));
        services.Configure<WorkflowObservabilityOptions>(configuration.GetSection("WorkflowObservability"));
        services.Configure<WorkflowDiscoveryOptions>(configuration.GetSection("WorkflowDiscovery"));
        services.Configure<OpenHandsOptions>(configuration.GetSection("OpenHands"));
        services.Configure<KubernetesJobOptions>(configuration.GetSection("KubernetesJobs"));
        services.AddSingleton<IPromptRenderer, FilePromptRenderer>();
        services.AddScoped<IGitHubAppClient, GitHubAppClient>();
        services.AddScoped<IGitHubClientFactory, GitHubClientFactory>();

        if (configuration.GetValue("UseFakeAdapters", true))
        {
            services.AddDbContext<FormicaeDbContext>(options => options.UseInMemoryDatabase("Formicae"));
            services.AddSingleton<IWorkflowStore, InMemoryWorkflowStore>();
            services.AddSingleton<IAiSettingsStore, InMemoryAiSettingsStore>();
            services.AddSingleton<IDevOpsIntegrationStore, InMemoryDevOpsIntegrationStore>();
            services.AddSingleton<IWorkflowOrchestrationLock, InMemoryWorkflowOrchestrationLock>();
            services.AddSingleton<IWorkItemProvider, FakeWorkItemProvider>();
            services.AddSingleton<ISourceControlProvider, FakeSourceControlProvider>();
            services.AddSingleton<IAgentRunner, FakeAgentRunner>();
            return services;
        }

        if (IsMode(configuration, "PersistenceMode", "InMemory"))
        {
            services.AddDbContext<FormicaeDbContext>(options => options.UseInMemoryDatabase("Formicae"));
            services.AddSingleton<IWorkflowStore, InMemoryWorkflowStore>();
            services.AddSingleton<IAiSettingsStore, InMemoryAiSettingsStore>();
            services.AddSingleton<IDevOpsIntegrationStore, InMemoryDevOpsIntegrationStore>();
            services.AddSingleton<IWorkflowOrchestrationLock, InMemoryWorkflowOrchestrationLock>();
        }
        else
        {
            services.AddDbContext<FormicaeDbContext>(options => options.UseNpgsql(configuration.GetConnectionString("Formicae")));
            services.AddScoped<IWorkflowStore, EfWorkflowStore>();
            services.AddScoped<IAiSettingsStore, EfAiSettingsStore>();
            services.AddScoped<IDevOpsIntegrationStore, EfDevOpsIntegrationStore>();
            services.AddSingleton<IWorkflowOrchestrationLock, PostgresWorkflowOrchestrationLock>();
        }

        if (IsMode(configuration, "WorkItemMode", "Fake"))
        {
            services.AddSingleton<IWorkItemProvider, FakeWorkItemProvider>();
        }
        else
        {
            services.AddScoped<IWorkItemProvider>(serviceProvider =>
                new GitHubWorkItemProvider(serviceProvider.GetRequiredService<IGitHubClientFactory>()));
        }

        if (IsMode(configuration, "SourceControlMode", "Fake"))
        {
            services.AddSingleton<ISourceControlProvider, FakeSourceControlProvider>();
        }
        else
        {
            services.AddScoped<ISourceControlProvider>(serviceProvider =>
                new GitHubSourceControlProvider(serviceProvider.GetRequiredService<IGitHubClientFactory>()));
        }

        if (IsMode(configuration, "AgentMode", "Fake"))
        {
            services.AddSingleton<IAgentRunner, FakeAgentRunner>();
        }
        else
        {
            services.AddSingleton<IKubernetesJobApi, KubernetesJobApi>();
            services.AddSingleton<IKubernetesJobRunner, KubernetesJobRunner>();
            services.AddSingleton<IAgentRunner, OpenHandsAgentRunner>();
        }

        return services;
    }

    private static bool IsMode(IConfiguration configuration, string key, string expected)
        => string.Equals(configuration[key], expected, StringComparison.OrdinalIgnoreCase);
}
