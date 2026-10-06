# References

## Repository

`WorkflowModels.cs`, `Interfaces.cs`, `WorkflowService.cs`, `WorkflowOrchestrator*.cs`: durable state, task retry, loop/decision/parallel history and launch/completion behavior.

`WorkerAgentMessageService.cs`, Worker `Program.cs`, `OpenHandsAgentRunner.cs`, `KubernetesJobRunner.cs` and container runtime: callbacks, worker streams, runtime output and cleanup boundaries.

API `Program.cs`, observability services and persistence stores: authorization, evidence and EF/in-memory behavior.

ClientApp `App.tsx`, `workflowGraph.ts`, `workflowEditor/*`: current detail polling, graph serialization, visual patterns and React Flow/ELK.

## Primary workflow-engine research

- [Temporal Web UI](https://docs.temporal.io/web-ui): execution search, pending work/history, evidence and controls.
- [Argo logs](https://argo-workflows.readthedocs.io/en/latest/cli/argo_logs/): follow, tail, timestamps and filters.
- [Argo archive](https://argo-workflows.readthedocs.io/en/latest/workflow-archive/): archived workflows exclude pod logs, requiring separate durable worker evidence.
- [Argo suspension](https://argo-workflows.readthedocs.io/en/latest/walk-through/suspending/): scheduling-boundary pause/resume.
- [GitHub workflow logs](https://docs.github.com/en/actions/how-tos/monitor-workflows/use-workflow-run-logs): job investigation, durations, downloads and permalinks.
- [n8n execution history](https://docs.n8n.io/build/understand-workflows/understand-executions/view-all-executions.md) and [debugging](https://docs.n8n.io/build/understand-workflows/understand-executions/debug-executions.md): filtered history and previous-execution evidence.
- [n8n execution-data management](https://docs.n8n.io/deploy/host-n8n/configure-n8n/scaling/manage-execution-data.md): persistence growth/retention constraints.

## Native capabilities

- [ASP.NET Core 10 SSE](https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-10.0?view=aspnetcore-10.0#support-for-server-sent-events-sse).
- [EF Core pagination](https://learn.microsoft.com/en-us/ef/core/querying/pagination).
- [React Flow accessibility](https://reactflow.dev/learn/advanced-use/accessibility).

[Argo retry policies](https://argo-workflows.readthedocs.io/en/latest/retries/) and [Temporal retry policies](https://docs.temporal.io/encyclopedia/retry-policies) inform future automatic retry policy. Preserve attempt history now; do not automatically repeat successful source-control side effects.
