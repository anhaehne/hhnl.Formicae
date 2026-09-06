# Per-step environment implementation contracts

Approved plan; implementation waits for the #19 commit. This extends existing models and helpers only. No new catalog, service, endpoint, database table, runtime policy, worker flag or migration is planned.

## Model and JSON additions

Append these optional parameters to WorkflowDefinitionStep, after all existing fields:

```csharp
[property: JsonPropertyName("environmentId")] string? EnvironmentId = null,
[property: JsonPropertyName("environmentSnapshot")] EnvironmentSnapshot? EnvironmentSnapshot = null
```

EnvironmentSnapshot is the existing #19 type. EnvironmentId null/omitted means inherit; `default` selects the virtual default; other exact IDs select catalog profiles. Empty/whitespace is invalid. An explicit override replaces the workflow profile and does not combine timeout caps. The document-level DefaultEnvironmentId/DefaultEnvironmentSnapshot remain unchanged and validated independently of node choices.

No AgentTask additions: its existing EnvironmentSnapshot field carries the selected node's resolved profile. No TaskRun additions: existing task-settings events persist the profile facts tied to exact task/attempt. JSON-backed definition storage preserves the new fields without EF model changes.

## Existing helper signatures remain stable

```csharp
EnvironmentDefinitions.ValidateConfiguration(EnvironmentConfiguration? configuration)
EnvironmentDefinitions.ResolveAsync(WorkflowDefinitionDocument document, EnvironmentService? environments, CancellationToken token)
EnvironmentDefinitions.ValidateRuntime(WorkflowDefinitionDocument document)
EnvironmentDefinitions.ResolveForTask(WorkflowDefinitionDocument document, WorkflowDefinitionStep step)
```

Return types remain those introduced in #19. Keep caching and structural comparison private to EnvironmentDefinitions; no resolver service or cache abstraction is necessary.

ResolveAsync creates one local exact-ID cache for the document default and every AI node. Default resolves virtually, custom IDs call EnvironmentService.GetAsync once, and misses are cached. Always resolve/validate the workflow default first even when all nodes override it. For each AI node, effective ID is step.EnvironmentId ?? document.DefaultEnvironmentId ?? `default`. Replace client snapshots with the authoritative cached profile, retaining the original EnvironmentId, including null inheritance. Missing/invalid IDs retain their selection and receive null snapshots for disabled-draft preservation.

Non-AI nodes: non-null EnvironmentId produces a node-referenced selection error, including `default` and empty strings. Strip submitted EnvironmentSnapshot during enrichment. Runtime rejects either field on non-AI nodes so manually persisted metadata cannot become active later. Do not erase invalid selections silently during server enrichment; the editor clears them only on a deliberate type change.

Use existing error code patterns, with node paths `steps[].environmentId` or `steps[].environmentSnapshot` and NodeId set. Document default errors retain their #19 paths. Existing WorkflowDefinitionService already merges environment errors and chooses enabled/disabled policy; do not duplicate that policy in the resolver.

## Runtime fallback and consistency

ValidateRuntime still validates the document default first. For each AI node:

1. No step ID and no step snapshot: legacy inheritance, use validated document snapshot or virtual default.
2. Explicit `default` and no snapshot: virtual default compatibility.
3. Explicit custom ID and no snapshot: invalid.
4. Present snapshot: validate selected/effective ID, name/description/revision, immutable default semantics and configuration. Present invalid snapshots never fall back.

Inherited present snapshots must match the pinned document default. Across present snapshots and the document default, the same ID must not carry conflicting revisions/name/description/configuration. Compare validated scalar fields and supported configuration semantically: schema version and runtime cap; null/empty runtime both mean no cap; image must be absent/null and tools/MCP empty after validation. Do not compare record collection references or raw JSON member ordering. This avoids false mismatches for JSON round trips while rejecting same-ID conflicting caps.

ResolveForTask uses the same validation and selects the pinned step snapshot when present, otherwise the permitted legacy/default fallback above. Return null for non-AI nodes. Common preparation already invokes this helper for sequential/Custom/Parallel launches, so new orchestration dispatch or adapter logic is unnecessary. Existing profile audit receives whichever snapshot this helper resolves.

## Frontend contracts

Extend WorkflowDefinitionStep and WorkflowStepNode data with environmentId/environmentSnapshot. Serialization, graph adapters and task duplication preserve both; changing to a non-AI type removes both. Include environmentId in ordinary field undo/dirty comparison; exclude environmentSnapshot as server metadata, alongside persona/custom snapshots.

Extend the existing EnvironmentPicker interface additively with optional label and inheritedId (or equivalent small discriminant), retaining workflow-default callers:

```ts
type EnvironmentPickerProps = {
  label?: string; // default "Workflow environment"; inspector uses "Step environment"
  inheritedId?: string | null; // supplied for step picker; undefined for workflow picker
  value?: string | null;
  environments: EnvironmentProfile[];
  savedSnapshot?: EnvironmentSnapshot | null;
  disabled: boolean;
  onChange: (id: string | undefined) => void;
};
```

Step picker null value shows Inherit workflow environment, explicit `default` remains a distinct option, other values show catalog selection. Pass workflow DefaultEnvironmentId ?? `default` as inheritedId. Preserve explicit empty-ID unavailable display using nullish checks rather than truthy fallback. The inspector is shown only for AI nodes; existing model/persona section provides the placement.

Saved step preview comes from savedDraft.nodes by stable ID, never current undoable snapshot. Saved document-default preview remains unchanged. A successful save merges server-returned step snapshots into the authoritative baseline. If edits occurred while saving, update a live node snapshot only when node type, environmentId and any inherited workflow default still match the submitted selection; never overwrite a later selection or make its preview claim a saved profile. Undo/redo may restore draft metadata but cannot change saved baseline previews.

Existing EnvironmentHistory already reads the resolved profile event; retain its profile-configuration wording and ensure tests show different task events with different environments. No new history parser, API endpoint or actual-job facts are introduced.

## Ownership and validation

- Parent/root: WorkflowModels step fields; EnvironmentDefinitions cache/selection/runtime validation; WorkflowDefinitionService changes only if needed; definition/compatibility tests; release version/docs and delivery.
- Frontend agent: API/node types, graph/state, picker/inspector, saved previews/history and browser tests.
- Runtime/test agent: focused orchestration integration tests proving ordinary/Custom/Parallel/loop/retry selection and audit; changes to existing preparation only if tests reveal a missing shared-helper call. Do not change adapter/worker policy behavior.
- Independent reviewer: inspect helper consistency, saved/draft semantics and final integration; no competing source edits without coordination.

Targeted definition and runtime tests, frontend build/browser tests, managed harness, full backend and existing Kubernetes release gate follow the plan. Verify no pending EF model changes. Builds remain serialized. Report exact added/edited/removed test counts.
