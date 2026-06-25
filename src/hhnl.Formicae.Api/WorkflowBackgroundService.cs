using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Api;

public sealed class WorkflowBackgroundService(IServiceScopeFactory scopeFactory, WorkflowTickNotifier notifier, ILogger<WorkflowBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await notifier.WaitForSignalOrDelayAsync(TimeSpan.FromSeconds(5), stoppingToken);
                await using var scope = scopeFactory.CreateAsyncScope();
                var orchestrationLock = scope.ServiceProvider.GetRequiredService<IWorkflowOrchestrationLock>();
                await using var lockHandle = await orchestrationLock.TryAcquireAsync(stoppingToken);
                if (lockHandle is null)
                {
                    logger.LogDebug("Skipped workflow orchestration tick because another instance holds the lock.");
                    continue;
                }

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