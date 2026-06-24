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
                var orchestrator = scope.ServiceProvider.GetRequiredService<WorkflowOrchestrator>();
                await orchestrator.AdvanceRunnableWorkflowsAsync(stoppingToken);
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
