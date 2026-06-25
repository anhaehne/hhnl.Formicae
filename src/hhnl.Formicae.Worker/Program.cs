using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddFormicaeInfrastructure(builder.Configuration);

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var discovery = scope.ServiceProvider.GetRequiredService<WorkflowDiscoveryService>();
var orchestrator = scope.ServiceProvider.GetRequiredService<WorkflowOrchestrator>();
var discovered = await discovery.DiscoverReadyToPlanWorkflowsAsync(CancellationToken.None);
var advanced = await orchestrator.AdvanceRunnableWorkflowsAsync(CancellationToken.None);
Console.WriteLine($"Discovered {discovered} workflow(s). Advanced {advanced} workflow(s).");
