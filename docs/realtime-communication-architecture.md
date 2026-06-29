# Realtime Communication Architecture

Status: planned architecture. This document recommends the realtime communication direction for workflow updates; it does not include an implementation.

## Current Runtime

The current runtime uses REST polling from the browser against the ASP.NET Core API. The API owns workflow orchestration and persistence, including workflow state, task runs, and worker message ingestion.

Agent work runs in ephemeral Kubernetes Jobs. The worker already sends HTTP callbacks to the API at `/api/worker/agent-messages`, and the API uses `WorkflowTickNotifier` to wake orchestration when external work or callbacks can advance a workflow.

## Recommendation

Use [ASP.NET Core SignalR](https://learn.microsoft.com/en-us/aspnet/core/signalr/introduction) for Server -> Browser realtime workflow updates. SignalR provides a browser-friendly abstraction over realtime transports and supports the group-based subscription model Formicae needs for workflow-scoped updates.

Keep REST endpoints as the authoritative read model. The browser should:

1. Fetch the current workflow snapshot through REST.
2. Open a SignalR connection with the [SignalR JavaScript client](https://learn.microsoft.com/en-us/aspnet/core/signalr/javascript-client).
3. Join workflow-scoped groups such as `workflow:{workflowId}`.
4. On realtime events, either patch local state from the payload or refetch the affected workflow data through REST.

Use HTTP callbacks for Worker -> Server communication. Treat worker callbacks as durable ingestion events that update persisted workflow state and audit data, not as browser-specific messages. Browser notifications are a projection of persisted server state.

Realtime worker output in the management UI should flow through the same projection path: the worker posts live agent messages to the API, the API persists and redacts them, then the API emits `agentMessage.appended` events to subscribed browser clients through SignalR.

## Event Contract

Realtime workflow events should use a shared envelope:

```json
{
  "eventId": "string",
  "sequence": 123,
  "workflowId": "string",
  "taskRunId": "string",
  "type": "workflow.updated",
  "occurredAt": "2026-06-29T00:00:00Z",
  "payload": {}
}
```

Expected event types:

- `workflow.updated`
- `taskRun.updated`
- `workflowLog.appended`
- `agentMessage.appended`
- `workflowSignal.raised`

Events must be emitted only after successful persistence. Consumers may use `sequence` to order events within a workflow and `eventId` for deduplication.

For live worker messages, `agentMessage.appended` is the browser-facing event. Its payload should contain only the persisted message fields needed by the management UI, such as message id, task run id, timestamp, role or stream name, and redacted content. It must not expose raw worker callback secrets, environment variables, credentials, or unredacted command output.

## Server To Browser

Future components:

- `WorkflowUpdatesHub` for browser SignalR connections.
- A workflow group subscription hub method or API endpoint that adds the current connection to `workflow:{workflowId}`.
- Authorization through the existing `WorkflowView` policy before any group subscription succeeds.

Reconnect behavior should be simple and recovery-oriented:

1. The client reconnects.
2. The client refetches the latest REST snapshot.
3. The client resubscribes to the workflow group.

This keeps realtime delivery opportunistic. REST remains the recovery source if events are missed while disconnected.

When the API ingests a worker callback for a workflow that is open in the management UI, the browser should receive the resulting `agentMessage.appended` event through the workflow group after the message has been committed. The UI may append the message locally for low-latency display and still refetch the workflow or task run snapshot when it detects a sequence gap, reconnects, or needs authoritative status.

Scale-out requirements follow the [SignalR scale-out guidance](https://learn.microsoft.com/en-us/aspnet/core/signalr/scale). A single API replica can run without a backplane. Multi-replica deployments require sticky sessions plus a SignalR backplane or a managed SignalR service so group membership and event fanout work across replicas.

## Server To Worker

Keep API-created Kubernetes Jobs as the Server -> Worker command path for now. Keep the worker HTTP callback as the Worker -> Server status and message path. This supports realtime management UI streaming without creating a direct Worker -> Browser channel: workers report to the API, and the API fans out persisted events to authorized browser subscribers.

Future hardening should add:

- HMAC request signatures instead of a static shared header.
- A timestamp and replay window.
- Event id and sequence number handling for idempotency.
- Bounded payload sizes.
- Retry with backoff from the worker.
- Batching or throttling for noisy stdout and frequent agent messages.

SignalR or raw WebSockets are not recommended for ephemeral workers unless interactive bidirectional control becomes a concrete requirement. The current worker lifecycle is job-oriented, and HTTP callbacks are easier to authenticate, retry, and reason about after worker restarts.

## Alternatives

[Server-Sent Events](https://developer.mozilla.org/en-US/docs/Web/API/Server-sent_events) are simpler for one-way browser push, but they are weaker for future bidirectional and group-based needs.

Raw WebSockets would require more protocol ownership with little benefit over SignalR for this product.

gRPC or gRPC-Web provide strong typed contracts, but they are heavier than needed for browser workflow notifications.

A message broker such as [RabbitMQ publish/subscribe](https://www.rabbitmq.com/tutorials/tutorial-three-dotnet), NATS, Redis Streams, or Kafka may be useful later for scale, replay, backpressure, or multi-service fanout. It is not required for the initial browser notification layer.

Kubernetes log watch is useful as a fallback and debugging tool, but it should not be the primary communication contract between worker, API, and browser.

## Reliability And Persistence

REST snapshots remain the recovery source if realtime events are missed. The UI must tolerate duplicate, out-of-order, and missing realtime messages by refetching authoritative workflow data when needed.

If exactly-once-ish delivery becomes important, prefer an outbox or workflow event table before pushing to SignalR. Persist the state change and outbound event in the same transaction, then have a dispatcher publish the event.

Do not rely on `TaskRun.Output` as the long-term event stream. Consider a dedicated workflow message or event table for append-only realtime and audit data, especially for agent messages, workflow logs, and workflow signals.

## Security

Browser hub subscriptions must require authenticated users with workflow read permission. Group names and workflow identifiers must not bypass authorization.

Worker callbacks must authenticate independently from browser and user authentication. Worker credentials should be scoped to worker callback ingestion only.

Never send secrets in realtime payloads. Apply the same redaction rules used for logs, prompts, and task output before persisting data and before broadcasting events.

## Testing Guidance

This is an architecture-only issue. Verify this change with documentation review.

Future implementation tests should cover:

- Event envelope serialization.
- Event emission after persistence.
- Hub authorization and group subscription.
- Reconnect and refetch behavior.
- Worker callback idempotency.
- Rejection of stale or invalid worker signatures.
