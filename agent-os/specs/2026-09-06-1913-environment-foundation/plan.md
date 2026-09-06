# Issue #19: reusable environment foundation

Status: implemented, independently reviewed and locally verified for 0.16.0; deployment verification follows the push. Shared interfaces are defined in [contracts.md](contracts.md). Current issue requirements were read from GitHub on 2026-09-06: operators create/select a reusable environment, configuration is validated before use, and MVP defaults remain available. This is the first environment-family delivery; it does not close sibling feature issues by proxy.

## Delivery boundaries

| Issue | Ownership |
| --- | --- |
| **#19, this delivery** | Reusable catalog, versioned configuration schema, immutable workflow-default snapshot, workflow-default selection, common runtime environment resolver, a bounded runtime timeout setting, validation, history/UI and preserved defaults. |
| #17 | Per-step environment override/inheritance/default opt-out and step-specific snapshot selection, reusing #19 catalog/resolver/history contracts. No second catalog or launcher. |
| #21 | Environment image references, pull policy/credential-reference validation and custom worker-image execution. Extend #19 configuration and mapper. |
| #22 | Ordered tool/bootstrap definitions, execution, bounded output and failure handling. Extend the same configuration/resolver. |
| #18 | Step-scoped secret references, selected-only materialization, transport/redaction/cleanup. No raw secrets in #19 catalog or snapshots. |
| #20 | Typed MCP server configuration and worker composition, relying on scoped credentials from #18. |
| #16 | Explicit enforceable capability policy and audit, including negative enforcement tests. #19 does not claim a security sandbox or alter permissions. |

Workflow-default selection in #19 satisfies selection from workflow definitions. Per-step selection is intentionally owned by #17. Release/closing documentation must say that images/tools/MCP are reserved extensibility areas awaiting their named issues, not usable features of #19.

## Catalog and schema

Add an `ExecutionEnvironment` catalog using the proven Persona/Custom Task patterns: server-generated immutable string ID, Name, Description, Revision, typed Configuration JSON, IsDeleted, CreatedAt and UpdatedAt. Names are trimmed/nonempty/max120; description max2,000. Viewers can list/inspect; ManagementAdmin creates/updates/soft-deletes. Edits/deletes use expectedRevision with atomic compare-and-swap and 409 conflicts. Unknown/deleted references return 404. No separate enabled toggle is needed for this foundation; soft deletion removes future availability while preserving snapshots.

Present a virtual immutable `default` environment at revision 1. It has empty override configuration and leaves current worker/runtime defaults unchanged; never seed a mutable row that could shadow it. Existing definitions with no environment fields retain identical behavior.

Configuration schema version 1:

```json
{
  "schemaVersion": 1,
  "runtime": { "timeoutLimitSeconds": null },
  "image": null,
  "tools": [],
  "mcpServers": []
}
```

The only executable configurable setting in #19 is optional `runtime.timeoutLimitSeconds`, integer 1–3,600. It caps a task's existing hard timeout and never increases it. Empty/null runtime means inherit. The schema explicitly reserves image/tools/MCP extension positions; in this release reject any non-null image, nonempty tools/MCP collection, unknown runtime setting or unsupported schema version with a clear feature-not-supported error. Never silently store active-looking configuration that execution ignores. Do not offer editable nonfunctional controls in the UI. Future issues replace these guarded empty positions with their own typed contracts and bump schema versions if compatibility requires it.

Configuration is a validated application document, not arbitrary Kubernetes YAML, a command, an environment-variable map, raw CLI TOML or a general extension dictionary. Bound serialized configuration to 32,768 UTF-8 bytes and reject malformed/null nested values deliberately. No arbitrary mount, privilege, service account, host networking, image pull secret creation or global configuration fields exist.

## Workflow selection and immutable configuration

Add optional `defaultEnvironmentId` and server-owned `defaultEnvironmentSnapshot` to WorkflowDefinitionDocument. The snapshot contains ID, Revision, Name, Description and Configuration. Null reference means `default`. This selection applies only to external AI task executions: Plan, Implement, AddressComments and Custom after #15. Direct CreatePullRequest operations and control nodes do not launch an environment and are unaffected.

New version saves resolve the workflow-default catalog ID once, ignore all supplied environment snapshots, validate the active catalog configuration, and persist the authoritative snapshot. Preserve all graph/editor/persona/task metadata. Unknown/deleted IDs remain visible in disabled drafts with no resolved snapshot; enabled saves reject them. A workflow with no AI tasks may still hold a valid default environment selection for later edits. The nonpersisting validation endpoint checks current selected references. Runtime validation uses only the pinned snapshot and never the mutable catalog.

Pinned versions remain executable after catalog edits/deletion. Saving another version deliberately captures current configuration/revision; the editor displays saved versus current revision. Legacy documents without selection/snapshot use the built-in default. Explicit custom references with absent, mismatched or malformed snapshots must fail instead of silently falling back. Catalog changes cannot alter the stored timeout cap for an existing version/run/retry.

The snapshot pins application-managed environment configuration, not deployment-global settings. A default image, cluster feature switch or operator platform timeout remains an existing platform control; #19 must not imply that saving a workflow freezes deployment image tags or overrides later operator policy. Image pinning/reference semantics belong to #21. History records the immutable selected profile constraints and labels them as configuration, not observed runtime facts.

## Shared runtime integration

Create one application-level environment selection/resolution contract consumed by the common agent preparation path. #19 reads the workflow-default snapshot; #17 later adds a per-step choice in that resolver without duplicating catalog or runtime code. Both sequential and Parallel Plan launches must pass through the same preparation contract. Keep durable ExecutionAttemptId, uncertain launch recovery and immutable task/persona snapshots intact.

Extend the existing AgentTask → OpenHandsAgentRunner → RuntimeJobSpec path with the resolved environment identity/configuration. Do not introduce another job launcher. Map only supported environment settings; unsupported combinations fail before job creation. Authentication/model discovery/setup jobs are platform operations and must not inherit arbitrary workflow environment selection.

Apply timeout limits where each runtime already resolves its effective execution policy. Calculate `effectiveTimeout = min(existingTaskOrRuntimeTimeout, environmentTimeoutLimit)` when a limit exists; preserve the exact existing policy when it does not. Existing checkpoint grace is clamped to `[0, effectiveTimeout - 1]`, matching current policy normalization. Custom tasks retain zero checkpoint grace. The cap does not request browser/DinD, add credentials or alter working directories.

Both Kubernetes and local-container adapters must expose the capped timeout to the worker and enforce it through their existing deadline mechanisms. Reuse #15's independent non-checkpoint worker deadline for capped non-commit AI tasks (such as Plan/Custom) and for any capped task whose effective checkpoint grace is zero, regardless of commit eligibility. In particular, a one-second cap makes Implement/AddressComments checkpoint grace zero and must still terminate their process tree at the hard deadline while scheduler polling is unavailable. Positive-grace Codex Implement/AddressComments jobs retain the existing checkpoint policy. OpenHands currently has no worker checkpoint path, so every capped OpenHands task uses the hard deadline even when its effective runtime checkpoint grace is positive; this does not introduce automatic OpenHands commits. Zero-grace capped Codex commit-capable tasks retain ordinary successful commit/push behavior within the hard deadline, but timeout does not start a checkpoint. This does not grant commit behavior to other task kinds. Default/no-cap jobs must preserve their existing timing behavior. When RuntimeJobSpec previously had no explicit ExecutionPolicy, a configured environment cap still needs worker timeout propagation; merely changing a Kubernetes deadline is insufficient for local parity.

Record only the selected environment ID/revision/name and immutable timeout cap in a profile-configuration event tied to the exact TaskRun and attempt. Name the field `timeoutLimitSeconds`, not `effectiveTimeoutSeconds`; it describes the saved constraint, not an observation of the external job. Reattaching after deployment-global settings change must retain this same profile audit and must not claim that recomputed image, timeout or browser/DinD settings describe an already running job. Actual runtime-fact inspection is outside #19 acceptance criteria and is not added here. If later work exposes actual settings, it must obtain them from the created/existing Kubernetes Job or container via the runtime result contract, or explicitly mark them unavailable; it cannot infer them from current defaults on reattach. Use the immutable workflow snapshot for historical configuration views after catalog deletion. Do not include SecretEnvironment/SecretFiles values or raw authentication data in audit serialization. Later #17/#21 extend this same history rather than inventing another stream.

## UI and compatibility

Add an Environments management page matching Personas/Custom Tasks: searchable list, selected form, revision/default badge, conflict retention, deletion confirmation explaining future availability, and view-only inspection for WorkflowView users. The editable configuration is name/description plus optional maximum task runtime. Display inherited/default behavior plainly; explain that the selected limit can shorten an existing task timeout and cannot enlarge it.

Add a workflow-default environment selector and preview in workflow settings. Show saved and current revision differences, deleted/unknown selections and current validation errors. Do not add per-node selection yet. Ensure defaultEnvironmentId/snapshot survive graph serialization, normalization, version switching, undo/redo and explicit saving. Preserve #14 delayed-save draft protection and all existing read-only behavior. Do not introduce autosave or mutable server drafts.

Legacy v1alpha1-3 documents remain compatible. Existing versions need no rewrite or backfill. The environment catalog requires a generated EF migration; snapshots remain in existing definition JSON. Add only the catalog table unless concrete runtime-history requirements cannot be represented by existing structured events; do not add a speculative execution table. Review migration/snapshot and test upgrading the latest released schema containing personas/custom tasks/decisions/parallel history.

## Validation and delivery gates

1. Catalog permissions, bounds, immutable default, optimistic edit/delete conflicts, soft deletion and real PostgreSQL concurrency/migration tests.
2. Configuration schema tests: valid/null cap, bounds, wrong types, malformed/null structures, unsupported image/tools/MCP fields, unknown keys/schema versions and payload limits. Unsupported configuration must never reach a job.
3. Definition tests: default parity, authoritative snapshots, ignored supplied snapshots, disabled unresolved drafts, current-reference validation, pinned execution after catalog edit/delete and graph/persona/task metadata preservation.
4. Runtime tests: same workflow default reaches sequential and Parallel Plan preparation; cap reduces but never raises task/runtime deadlines; no-cap preserves exact RuntimeJobSpec defaults; Custom remains non-checkpointing; auth/model-discovery operations remain unaffected. Change deployment defaults between launch and durable reattachment and assert the audit still describes the same pinned profile constraints without fabricated actual image/provisioning/timeout fields.
5. Real worker/container and Kubernetes tests for capped execution, process termination and local/Kubernetes parity, including a cap when the prior ExecutionPolicy was null. Exercise both CLI paths with Plan/Custom and one-second Implement/AddressComments caps whose effective checkpoint grace becomes zero; verify hard process termination while scheduler polling is absent. Also test positive-grace capped OpenHands hard termination and preserved positive-grace Codex checkpoint selection. Verify no new credential/browser/DinD exposure and unchanged no-cap timing.
6. Browser management/default-selection/save-reload/conflict/read-only/unknown-selection coverage and desktop/narrow screenshots. Retain delayed-save protection.
7. Independent review; targeted/full backend, frontend build/smoke, managed harness and required migration/runtime Kubernetes E2E. Select the next available minor release at implementation time, align versions once, commit main/push, verify deployment and close #19 with exact delivered foundation and deferred sibling features.

All validation uses isolated local development/test resources or the established release pipeline. This plan authorizes no manual live infrastructure, registry credentials, mounts, cluster policies or global configuration mutation.
