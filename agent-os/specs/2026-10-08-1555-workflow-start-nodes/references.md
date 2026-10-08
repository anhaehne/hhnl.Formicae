# References

- `src/hhnl.Formicae.Application/Workflows/WorkflowNodeDefinitions.cs`: control-node validation and compilation.
- `src/hhnl.Formicae.Application/Workflows/WorkflowGraphDefinitions.cs` and `WorkflowOrchestrator.Graph.cs`: reachability and active dependency scheduling.
- `src/hhnl.Formicae.Application/Workflows/WorkflowTriggerService.cs`: provider starts, deduplication and audit.
- `src/hhnl.Formicae.Api/ClientApp/src/workflowGraph.ts` and `workflowEditor/`: editor-only legacy adaptation and node settings.
- `src/hhnl.Formicae.Api/Program.cs`: ASP.NET endpoints and signed provider webhook ingress.

- [ASP.NET Kestrel request-size options](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/options?view=aspnetcore-10.0): native per-request `IHttpMaxRequestBodySizeFeature`; `EnableBuffering` provides the bounded fallback used by test hosts.
