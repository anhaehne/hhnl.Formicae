# Task and decision issue implementation plans

Status: planning findings for independent review; no implementation approved by this document alone. The user authorized progressing open issues with reviewed plans, implementation, verification, deployment, and solution comments.

## Dependency order and shared constraints

- Implement #11 after #10 establishes durable graph scheduling.
- #14 personas is independent of graph scheduling; implement before #15 custom tasks.
- Implement #12 scripts after capability/environment resolution (#16–19), so scripts inherit explicit isolation and credential policy.
- Agree on durable execution identity and immutable configuration snapshots before each implementation. Identify task executions by definition node plus branch/loop execution, never by task kind alone.
- Preserve all built-in task behavior, legacy definitions, saved editor metadata, explicit saving, and read-only permissions.
- Catalog edits must not change existing immutable workflow versions or resumed runs. Snapshot referenced catalog configuration when saving a version.
- These are proposed semantics; the reviewing agent must resolve any changes before implementation.

## #11 Decisions and branches

Add a `builtins.decision` control node with one typed condition and required True and False targets. Binary outcomes avoid ambiguous matching rules. Sources are configured literal input, allowed workflow fields, or named completed predecessor output. Support equals, not-equals, contains, exists, and explicit numeric comparisons; do not evaluate arbitrary code.

Resolve output sources by execution/branch/loop iteration. Persist the result and selected target before advancing, atomically with scheduler state. Recovery reuses the persisted outcome. Output references must dominate the decision, or the definition must specify deterministic missing-value behavior. Validate targets, references, operators/value types, reachability, and cycles.

Integrate with #10's scheduler rather than extending only editor ports: current v1alpha3 normalization produces a linear v1alpha2 execution plan, and the orchestrator accepts only four task kinds. Add catalog entry, True/False handles, condition inspector, summary, undo, saved layout, and auditable run history.

Tests: true/false outcomes; missing and invalid data; recovery does not reevaluate; branch convergence; loop-scoped output; invalid references/cycles; browser create/connect/edit/save/history. Definition JSON needs no migration. Durable control execution persistence should use #10's execution model; WorkflowEvents may expose history but are not a replacement for transactional scheduler state.

## #14 Customizable personas

Add a management catalog with ID, name, instructions, tone, operating constraints, and revision. Preserve a built-in default with existing behavior. Select a workflow default and per-task override; null inherits. Script/control nodes cannot select an agent persona.

Snapshot the resolved persona into immutable definition versions. Compose persona context with existing task instructions rather than replacing them. The common StartAgentTaskAsync path covers Plan, Implement, and AddressComments. CreatePullRequest currently invokes the provider directly and does not launch an agent.

Add an operator management page and inheritance-aware selectors; reject unknown references when saving enabled versions. Persist catalog configuration in a new table; definition snapshots remain JSON. Include persona ID/revision in task-start audit details.

Tests: default parity, workflow inheritance, task override, unknown/deleted references, immutable snapshots, prompt context, permissions, and browser catalog/editor persistence.

## #15 Customizable tasks

Add a reusable task catalog separate from TaskRunKind: name, prompt template, typed inputs (string/number/boolean with required/default), and runner configuration. Initially support the agent runner. Add a `builtins.custom-task` node with task reference/revision and input values; snapshot catalog data in workflow versions.

Create a generic orchestration path. Mapping custom tasks to Implement would incorrectly inherit planning state and side effects. Resolve templates using an explicit token allowlist. Validate missing/unknown inputs and types before execution; provide inputs as structured context and rendered prompt. Persist output and task ID/revision plus resolved non-secret inputs. Define bounded output/failure behavior.

Custom tasks must not implicitly post comments, open pull requests, or alter the built-in task lifecycle. Use a new catalog table. Append persisted enum values rather than reordering them. Execution snapshots can share metadata introduced by #10.

Tests: reuse, required/default/type validation, prompt inputs, persisted outputs, failures/restarts, snapshot stability, persona composition, and browser catalog/select/save.

## #12 Scriptable steps

Add `builtins.script` with script text, supported shell (`/bin/sh` initially), repository-relative working directory, timeout, and explicit environment references. Validate path traversal and bounds. Execute in the existing worker/job runtime with a script branch before AI authentication or CLI launch. Scripts must not require AI settings or receive AI credentials.

Mount script content as a context file and invoke the fixed shell through argument-list APIs. Do not interpolate configuration into a composed host command. Capture stdout/stderr, numeric exit code, timeout/cancellation reason, and persisted output. Exit zero succeeds, nonzero fails, and timeout/cancellation kills the process tree.

Repository checkout is explicit/optional. Scripts do not automatically commit or push. Capability/environment policy governs repository credentials, browser, nested containers, and secrets. OpenHandsAgentRunner currently always resolves AI settings, so introduce a dispatcher/common job builder instead of routing scripts through it unchanged.

Append a Script task kind and store exit code (or structured task-result JSON shared with #15); generate and test the required migration. Tests cover deterministic success/output, stderr/nonzero, timeout/cancellation, working-directory rejection, no AI credentials, environment selection, runtime manifests, and browser creation/edit/save. Run worker/container and Kubernetes E2E because execution changes.
