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
        services.AddScoped<AiSettingsService>();
        services.AddSingleton<IClock, SystemClock>();
        services.Configure<WorkflowObservabilityOptions>(configuration.GetSection("WorkflowObservability"));
        services.Configure<WorkflowDiscoveryOptions>(configuration.GetSection("WorkflowDiscovery"));
        services.Configure<OpenHandsOptions>(configuration.GetSection("OpenHands"));
        services.Configure<KubernetesJobOptions>(configuration.GetSection("KubernetesJobs"));
        services.AddSingleton<IPromptRenderer, FilePromptRenderer>();

        if (configuration.GetValue("UseFakeAdapters", true))
        {
            services.AddSingleton<IWorkflowStore, InMemoryWorkflowStore>();
            services.AddSingleton<IAiSettingsStore, InMemoryAiSettingsStore>();
            services.AddSingleton<IWorkflowOrchestrationLock, InMemoryWorkflowOrchestrationLock>();
            services.AddSingleton<IWorkItemProvider, FakeWorkItemProvider>();
            services.AddSingleton<ISourceControlProvider, FakeSourceControlProvider>();
            services.AddSingleton<IAgentRunner, FakeAgentRunner>();
            return services;
        }

        if (IsMode(configuration, "PersistenceMode", "InMemory"))
        {
            services.AddSingleton<IWorkflowStore, InMemoryWorkflowStore>();
            services.AddSingleton<IAiSettingsStore, InMemoryAiSettingsStore>();
            services.AddSingleton<IWorkflowOrchestrationLock, InMemoryWorkflowOrchestrationLock>();
        }
        else
        {
            services.AddDbContext<FormicaeDbContext>(options => options.UseNpgsql(configuration.GetConnectionString("Formicae")));
            services.AddScoped<IWorkflowStore, EfWorkflowStore>();
            services.AddScoped<IAiSettingsStore, EfAiSettingsStore>();
            services.AddSingleton<IWorkflowOrchestrationLock, PostgresWorkflowOrchestrationLock>();
        }

        if (IsMode(configuration, "WorkItemMode", "Fake"))
        {
            services.AddSingleton<IWorkItemProvider, FakeWorkItemProvider>();
        }
        else
        {
            services.AddSingleton<IWorkItemProvider>(_ => new GitHubWorkItemProvider(CreateGitHubClient(requireToken: false)));
        }

        if (IsMode(configuration, "SourceControlMode", "Fake"))
        {
            services.AddSingleton<ISourceControlProvider, FakeSourceControlProvider>();
        }
        else
        {
            services.AddSingleton<ISourceControlProvider>(_ => new GitHubSourceControlProvider(CreateGitHubClient(requireToken: true)));
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

    private static GitHubClient CreateGitHubClient(bool requireToken)
    {
        var client = new GitHubClient(new ProductHeaderValue("hhnl-formicae"));
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            if (requireToken)
            {
                throw new InvalidOperationException("GITHUB_TOKEN is required for GitHub source control operations.");
            }

            return client;
        }

        client.Credentials = new Credentials(token);
        return client;
    }
}
