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

        services.AddDbContext<FormicaeDbContext>(options => options.UseNpgsql(configuration.GetConnectionString("Formicae")));
        services.AddScoped<IWorkflowStore, EfWorkflowStore>();
        services.AddHttpClient<IWorkItemProvider, GitHubWorkItemProvider>();
        services.AddHttpClient<ISourceControlProvider, GitHubSourceControlProvider>();
        services.AddSingleton<IKubernetesJobRunner, KubernetesJobRunner>();
        services.AddSingleton<IAgentRunner, OpenHandsAgentRunner>();
        return services;
    }
}
