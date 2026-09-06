# Issue #17: per-step execution environments

Implemented, independently reviewed and locally verified for 0.17.0; deployment verification follows the push. This builds on the #19 foundation and the issue read on 2026-09-06. Acceptance: different steps use different configured environments; selection persists in workflow history; invalid references fail before execution.

## 1. Save the specification

Save this plan, scope decisions, applicable standards and existing implementation references before source edits. No new visuals were supplied; use the existing workflow inspector and environment picker design. Obtain independent agent review, resolve feedback, and have the parent approve before implementation.

## 2. Add step selection without another catalog

Append optional `environmentId` and server-owned `environmentSnapshot` to WorkflowDefinitionStep. Keep the #19 workflow default and catalog unchanged. Apply selection only to existing external AI task types: Plan, Implement, AddressComments and Custom. Direct CreatePullRequest and Trigger/Loop/Parallel/Decision controls have no worker execution environment of their own. Individual Plan tasks inside Parallel groups and AI tasks inside loops can select profiles normally.

Selection semantics:

| Step value | Effective profile |
| --- | --- |
| omitted/null | Inherit the workflow default; absent workflow selection means built-in default. |
| `default` | Explicitly use the built-in default, opting out of a custom workflow default. |
| active catalog ID | Use that profile instead of the workflow default. |
| empty/whitespace/unknown/deleted ID | Validation error; never silently inherit. |

An override replaces the workflow profile; it does not combine caps. For example, workflow cap30 and step cap60 selects cap60, which still cannot exceed the task/runtime timeout. Explicit default removes the workflow cap but preserves platform/task defaults. Explain this in the selector. The runtime still enforces only the #19 timeout cap; images, tools, MCP, capabilities and secrets remain their own issues.

The workflow default must remain valid even when all AI nodes override it, or the graph contains no AI tasks. It is a stored selectable configuration for later edits, and #19 already validates it. Do not make document validity depend on whether any current task happens to inherit.

## 3. Resolve and persist authoritative snapshots

Extend the single EnvironmentDefinitions.ResolveAsync pass to resolve the workflow default and every AI step's effective reference using one cache keyed by exact ID. Read each distinct custom ID once across the whole document, including IDs used by both workflow default and steps. Resolve the virtual default without catalog reads. Cache missing references too. Ignore all submitted snapshots and capture current authoritative profiles for every AI step on each new version save.

Preserve both the selected environmentId and resolved environmentSnapshot in each saved AI node. Inheriting nodes retain null environmentId and receive the workflow-default snapshot. Explicit default receives the immutable default snapshot. Snapshot all AI nodes, so history can display their resolved profile even without re-running inheritance logic. Keep the document-level default snapshot for #19 compatibility and settings previews.

Enabled saves combine environment errors with graph/persona/custom-task validation and reject invalid references before persistence. Disabled saves retain unresolved IDs and other edits, remove unresolved snapshots and return node-referenced validation feedback. Environment selection on non-AI nodes is invalid even if the value is `default`; remove client-supplied non-AI snapshots during save enrichment, and reject persisted non-AI environment metadata during runtime validation. Type-changing editor operations clear both step fields.

The nonpersisting validation endpoint checks current references through this same cached resolver; it must not trust submitted snapshots. Saving another version deliberately captures current profile revisions. Old versions remain unchanged after catalog edits/deletion.

## 4. Extend the shared runtime resolver and audit

EnvironmentDefinitions.ValidateRuntime validates the document default and each AI node's selected/effective pinned profile without catalog reads. Check IDs, revision, immutable default semantics, configuration schema and supported settings before launching any task. For new inherited snapshots, require consistency with the document's pinned default; identical IDs captured across a new version must not carry conflicting profile revisions/configuration. Use structural configuration comparison, not record reference equality for JSON collection fields.

Preserve legacy documents from #19 and earlier: a node with neither selection nor snapshot inherits the validated document default, falling back to virtual default for truly legacy documents. A node with an explicit custom ID and no matching snapshot is invalid. Explicit `default` with absent snapshot can resolve the virtual default, matching document-default compatibility. A present malformed snapshot never causes fallback.

Change only EnvironmentDefinitions.ResolveForTask(document, step) to choose the validated effective step snapshot or legacy inheritance. The existing common PrepareAgentTaskAsync already feeds ordinary, Custom and Parallel paths and retains the stable attempt/context/model/persona fields. No new launcher, worker flag, RuntimeJobSpec field or environment catalog API is required. Loops use the same pinned step profile for every iteration. Explicit retry and uncertain launch recovery reuse pinned profile selection, never current catalog data.

The existing AgentSettingsResolved `environment` object already records ID/revision/name/timeoutLimitSeconds against the exact task run and attempt. Ensure it now receives the resolved step profile for every path. It remains configuration history, not measured runtime facts. Add a readable selected environment summary to task history using that existing event data and pinned node context; older events without environment metadata remain readable. Test two ordinary nodes and two Parallel branches with different caps, explicit default opt-out, loop/retry stability after catalog deletion, and unchanged direct PR behavior.

## 5. Inspector, saving and history

Reuse the environment picker in each AI task inspector. Present Inherit workflow environment, Default environment and catalog entries. Show the effective inherited/default profile, timeout cap and current-versus-saved revision information. Missing/deleted selections remain visible with an explanation that pinned versions still execute but a new enabled save requires an active selection.

Use state.savedDraft keyed by stable node ID for saved profile previews. Do not let undoing an old field edit make the saved profile revision look older than the last successful save. Step environmentSnapshot is server-owned metadata excluded from dirty comparisons; environmentId is an ordinary undoable field. Delayed saves update the saved baseline while preserving any later selection changes. Copy both fields when duplicating tasks, then server re-resolves snapshots on save. Clear both when switching to a non-AI node type. Normalize/serialize/switch versions without dropping metadata.

Workflow settings keep their existing default selector and saved preview. A step override does not mutate the workflow default or another node. Read-only users can inspect selected/current/saved profiles but cannot change selection. Narrow layouts reuse the existing inspector drawer. No new permanent canvas panel is introduced.

## 6. Verification and delivery

- Definition tests cover all three selection modes, same-ID resolution once across document/steps, missing cache reuse, authoritative snapshots, disabled drafts, invalid IDs on control/direct-PR nodes, malformed/default snapshot guards, legacy fallback and snapshot isolation after edit/delete.
- Serialization tests cover normalization, duplicate tasks, version switching and document defaults even when all tasks override. No database model change is expected because definition JSON stores both new fields; verify EF has no pending model changes.
- Runtime tests cover ordinary/Custom/Parallel task selection and profile audit, loop iterations, retry/reattach after catalog edits, explicit-default opt-out, invalid references before launch and preserved model/persona/attempt identity. Existing deadline tests remain the enforcement baseline; re-run relevant runtime tests without changing worker timing behavior.
- Browser tests cover per-step differing selections, inheritance/default override, save/reload, saved/current preview after catalog update, delayed save plus later edit, field undo, duplication, non-AI metadata removal, read-only inspection and clickable validation errors. Inspect desktop/narrow screenshots.
- Run targeted/full backend, frontend build/smoke and managed harness. Run the established Kubernetes E2E gate for the runtime integration and release, while keeping infrastructure mutation within the existing release pipeline.
- Independent implementation review, aligned minor release files/docs, commit main/push, verify deployment and close #17 with delivered semantics and exact added/edited/removed test counts. Deployment and closing follow the user's existing session authorization; no unrelated live mutation is introduced.
