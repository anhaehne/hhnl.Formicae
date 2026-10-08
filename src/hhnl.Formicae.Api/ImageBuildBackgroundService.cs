using hhnl.Formicae.Application.Images;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Images;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Api;

public sealed class ImageBuildBackgroundService(IServiceScopeFactory scopes, IOptions<ManagedImageOptions> options,
    ILogger<ImageBuildBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
        while(!token.IsCancellationRequested)
        {
            try
            {
                using var scope=scopes.CreateScope();
                await using var handle=await scope.ServiceProvider.GetRequiredService<IWorkflowOrchestrationLock>().TryAcquireAsync(token);
                if(handle is not null) await scope.ServiceProvider.GetRequiredService<ImageBuildCoordinator>().TickAsync(options.Value.Concurrency,TimeSpan.FromSeconds(options.Value.TimeoutSeconds),token);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
            catch(Exception exception){logger.LogError(exception,"Managed image build reconciliation failed.");}
            if(!await timer.WaitForNextTickAsync(token)) break;
        }
    }
}
