# Workflow event nodes

Version 0.22.0 represents workflow entrypoints as event nodes. Each event has a fixed type, its own configuration under `event`, and one outgoing connection to the first execution node. Event nodes have no input port and launch no worker. There is no universal event node or provider/type selector.

## Built-in and integration events

A definition may contain one built-in Start event. Its ID is `startStepId`; manual runs enter through its connection. Definitions without a Start event use an empty `startStepId` and are started by external events only. Disabled Start events reject manual runs. The manual workflow selector includes only enabled versions with an enabled manual entry, while legacy versions keep their existing manual-start behavior.

The Add Step catalog has separate entries: **Start** (`builtins.start`), **Webhook** (`builtins.webhook`), **GitHub: Issue created** (`github.issue-created`), **GitHub: Label added** (`github.label-added`), and **Gitea: Label added** (`gitea.label-added`). GitHub issue-created events match signed `issues/opened` deliveries; label-added events match `issues/labeled` and the configured label. Provider events select repositories from their own integration provider and allow branch/model overrides. Issue created has no label field. Azure DevOps remains future work.

The shared `IWorkflowEventDefinition` contract provides metadata, configuration compilation, validation and delivery matching. An integration registers its definitions with `WorkflowEventRegistry` during application composition before requests are served. The API exposes this catalog at `GET /api/workflow-events`; the editor derives catalog entries and settings from those registered definitions. The GitHub and Gitea integrations own their registrations. Existing legacy compiler/audit types are compatibility boundaries, not editor choices.

## Webhook deliveries

Set the node's **Webhook secret name** to an operator-managed configuration name, for example `build-hook`. The API reads its value from `WorkflowWebhooks:Secrets:build-hook` (environment variable `WorkflowWebhooks__Secrets__build-hook`). The definition stores the name only. No endpoint returns the secret value.

After saving an enabled version, the inspector displays `/api/webhooks/workflows/{versionId}/{nodeId}`. Send a POST over HTTPS with `Authorization: Bearer <configured secret>`, a unique `X-Formicae-Delivery` header of at most 128 characters, and JSON containing `issueUrl`, `repositoryUrl`, and optionally `baseBranch` and `model`. The saved node's branch/model overrides take precedence. The endpoint always pins its path's version and node; payload definition IDs cannot redirect the run. Payloads are bounded to 65536 bytes.

Responses: 202 for a new run; 200 with the original workflow ID for an already accepted delivery/node pair; 409 if the issue already has a workflow; 401 for missing or incorrect authentication or an unconfigured secret; 404 for disabled/missing versions or non-webhook nodes; 400 for invalid payloads or missing delivery IDs; 413 for oversized bodies; 415 for unsupported content types. A busy scheduler lock returns 503; retry with the same delivery ID. Disabling a version stops new deliveries for that path without changing existing runs.

## Scheduling, history and compatibility

Each execution follows its selected start's reachable work. Graph joins wait for all active predecessors, without waiting for branches exclusive to another start. Typed input bindings must have a producer available from every entry that reaches the consumer. Existing loop, decision and explicit parallel restrictions continue to apply.

The durable WorkflowQueued event records `eventNodeId`, compatibility `startNodeId`, and `entryStepId`; execution investigation marks the selected event as Started. External starts also retain delivery audit links. Retry and restart use the pinned definition and existing durable task/graph activation state.

Legacy saved versions and historical execution graphs remain readable without rewriting. Opening a legacy definition for editing adds a collision-safe manual Start event and adapts legacy trigger/start settings in the draft. Legacy label nodes that can select repositories from both providers use a compatibility event definition, preserving their matching behavior without offering that definition in the Add Step catalog. Saving creates a new immutable version; the original stays unchanged. The built-in template includes Start, and startup upgrades the existing simple built-in workflow by creating one new version under the orchestration lock. Existing loops/top-level triggers are adapted through the editor rather than rewritten at startup.
