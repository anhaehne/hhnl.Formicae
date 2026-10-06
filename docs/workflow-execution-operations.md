# Workflow execution operations

Version 0.19.0 adds an execution investigator for running and historical workflows. Open an execution from workflow history to inspect its pinned definition as a read-only node graph. Editing a definition later does not change the graph of an existing execution.

## Investigate a task

Select a graph node, then choose its loop iteration and execution attempt. The inspector presents task state, timing, worker identity, failure details, prepared inputs, outputs and producer provenance. Decision evidence explains the route actually selected. Nodes that did not execute are distinct from failed tasks. Execution counts describe work observed so far; conditional branches and loops do not imply a fixed percentage complete.

Task logs are separate from authoritative task output. Worker stdout, stderr and worker errors are retained with task and attempt identities. Stream, severity and text filters apply to history, streaming and downloads. Load older records to investigate earlier activity, or follow new activity through the authenticated server-sent event stream. Pausing follow stops automatic updates without pausing the worker. Connection status reports reconnects; cursor-based replay recovers persisted records after a disconnect.

Use investigation links to return to the same workflow, node and attempt. Workflow history supports text, status, definition, repository and date filters, Running/Failed presets and locally saved views. Evidence export includes the pinned definition, attempts, events, decisions and typed task evidence; it does not include provider credentials. Log and evidence downloads are bounded and report omitted earlier records.

## Control scheduling

Pause takes effect at a scheduling boundary. Already running workers continue, including parallel workers, and their evidence remains available. Resume permits subsequent tasks to start. Pause does not freeze processes inside a worker.

Cancel first persists cancellation intent under the scheduler lock. The scheduler then preserves available partial logs and terminates active runtimes. Cancellation is complete only after runtime reconciliation succeeds. Repeated cancellation requests are safe; cleanup failures remain pending for retry after restart.

Retry preserves the replaced attempt before starting another. A task cannot retry while its previous runtime cleanup is pending. Prior attempt logs and task evidence remain available after retry.

## Persistence and operation

Workers deliver bounded, retryable log callbacks with stable message identities. Runtime logs provide a fallback when callbacks cannot reach the API. Duplicate records are suppressed; overflow and truncation are marked. Callbacks from a stale attempt cannot replace task output or append to the current attempt.

Completion processing captures runtime logs before acknowledging cleanup. Kubernetes jobs remain available until this acknowledgment when automatic finished-job deletion is enabled. Cancellation terminates a running job even when finished-job retention is enabled.

The API uses ASP.NET Core's native SSE support with authenticated workflow-view access. Reverse proxies must permit long-running responses and disable buffering for the log stream. Keep the worker callback endpoint reachable from the runtime. Database logs survive worker removal and require an operational retention policy appropriate to deployment volume; automatic retention pruning is future work.

Known provider credentials are masked before worker telemetry is written. Task code can still print unrelated secrets; avoid printing sensitive values in task output or scripts.

## API

Execution history: `GET /api/workflows/search`. Pinned run investigation: `GET /api/workflows/{id}/execution`. Logs: `GET /api/workflows/{id}/logs/page`, `/logs/stream` and `/logs/download`. Evidence: `GET /api/workflows/{id}/evidence`. Scheduling controls: `POST /api/workflows/{id}/pause`, `/resume` and `/cancel`.

Log history supports `after` or `before` cursors, task/attempt identity, source, level and text filters. SSE accepts `Last-Event-ID` for reconnection. Viewing requires workflow-view permission; scheduling controls require workflow-operate permission.
