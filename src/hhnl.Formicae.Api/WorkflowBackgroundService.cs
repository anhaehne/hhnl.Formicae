using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Api;

public sealed class WorkflowBackgroundService(IServiceScopeFactory scopeFactory, ILogger<WorkflowBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var discovery = scope.ServiceProvider.GetRequiredService<WorkflowDiscoveryService>();
                var orchestrator = scope.ServiceProvider.GetRequiredService<WorkflowOrchestrator>();
                var discovered = await discovery.DiscoverReadyToPlanWorkflowsAsync(stoppingToken);
                var advanced = await orchestrator.AdvanceRunnableWorkflowsAsync(stoppingToken);

                if (discovered > 0 || advanced > 0)
                {
                    logger.LogInformation("Workflow tick discovered {DiscoveredWorkflowCount} workflow(s) and advanced {AdvancedWorkflowCount} workflow(s).", discovered, advanced);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Workflow orchestration tick failed.");
            }
        }
    }
}
