using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Infrastructure.GitHub;
using hhnl.Formicae.Infrastructure.Kubernetes;
using hhnl.Formicae.Infrastructure.OpenHands;
using hhnl.Formicae.Infrastructure.Persistence;
using hhnl.Formicae.Infrastructure.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace hhnl.Formicae.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddFormicaeInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<WorkflowService>();
        services.AddScoped<WorkflowOrchestrator>();
        services.AddScoped<WorkflowDiscoveryService>();
        services.Configure<WorkflowDiscoveryOptions>(configuration.GetSection("WorkflowDiscovery"));
        services.Configure<OpenHandsOptions>(configuration.GetSection("OpenHands"));
        services.Configure<KubernetesJobOptions>(configuration.GetSection("KubernetesJobs"));
        services.AddSingleton<IPromptRenderer, FilePromptRenderer>();

        if (configuration.GetValue("UseFakeAdapters", true))
        {
            services.AddSingleton<IWorkflowStore, InMemoryWorkflowStore>();
            services.AddSingleton<IWorkItemProvider, FakeWorkItemProvider>();
            services.AddSingleton<ISourceControlProvider, FakeSourceControlProvider>();
            services.AddSingleton<IAgentRunner, FakeAgentRunner>();
            return services;
        }

        if (IsMode(configuration, "PersistenceMode", "InMemory"))
        {
            services.AddSingleton<IWorkflowStore, InMemoryWorkflowStore>();
        }
        else
        {
            services.AddDbContext<FormicaeDbContext>(options => options.UseNpgsql(configuration.GetConnectionString("Formicae")));
            services.AddScoped<IWorkflowStore, EfWorkflowStore>();
        }

        if (IsMode(configuration, "WorkItemMode", "Fake"))
        {
            services.AddSingleton<IWorkItemProvider, FakeWorkItemProvider>();
        }
        else
        {
            services.AddHttpClient<IWorkItemProvider, GitHubWorkItemProvider>();
        }

        if (IsMode(configuration, "SourceControlMode", "Fake"))
        {
            services.AddSingleton<ISourceControlProvider, FakeSourceControlProvider>();
        }
        else
        {
            services.AddHttpClient<ISourceControlProvider, GitHubSourceControlProvider>();
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
