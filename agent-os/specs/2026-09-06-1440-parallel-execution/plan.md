# Issue #10: structured parallel planning (0.12.0)

## Accepted scope
Add a first-class Parallel control node with 2–8 named branch outputs, a Join input and one Next continuation. Each branch is a disjoint sequential chain of Plan tasks ending at that Join. This meets the issue requirement for independent concurrent steps without allowing concurrent writes to the shared implementation branch. Reject Implement, Create PR, Address comments, nested parallel/loops, overlapping regions and external entry into branch bodies. Existing loops may occur before or after a parallel group; groups cannot be placed inside loop bodies in this release. Document these restrictions in the editor, API errors and closing comment.

## Execution and persistence
Persist a parallel activation keyed by workflow and node, with entry PlanArtifact snapshot, status and timestamps. The immutable version defines branch ownership/order. Existing per-step TaskRun rows persist branch progress; successful predecessor output provides branch-local prompt context, falling back to entry snapshot. A group is activated once, then the global cursor remains on its Parallel node until the barrier completes.

Schedule eligible tasks serially through the scoped store so external jobs overlap without parallel EF calls. Poll every running branch each tick. A failed branch stops its own suffix; unaffected branches finish their chains. Only after all branches are either succeeded or failed does the workflow fail or join. Failures identify branch and task. On success aggregate terminal branch outputs in configured order, publish with a stable workflow/group comment marker and advance the cursor once. Recovery from a completed activation must advance without re-launching branches.

Persist an optional execution-attempt GUID on TaskRun. Parallel AgentTask receives this durable identity; the runtime derives a stable job name and attaches on AlreadyExists. Resume launch uncertainty using the same identity. Explicit retry renews only failed attempts and preserves succeeded tasks. Workflow retry resets all failed branch tasks in the active group; task retry resets the selected failed branch, keeping cursor on Parallel. Both retry endpoints acquire the existing orchestration lock before reading/mutating.

## Delivery sequence
1. Add DSL settings, structured validator/compiler, activation persistence and generated EF migration; preserve v1alpha1–3 compatibility.
2. Add scheduling/polling/join/retry and stable external launch identities. No concurrent EF calls.
3. Add catalog, named handles, inspector branch controls, adapters/layout/validation and history visibility using existing task rows/events.
4. Add deterministic success/failure/out-of-order/restart/retry/input-isolation tests; real PostgreSQL migration/persistence tests; browser editing and all-node rendering coverage.
5. Review implementation independently. Run backend suite, frontend build/browser, managed harness and Kubernetes E2E/CI. Align 0.12.0 versions/docs. Commit main, push, wait for healthy deployment, then close #10 with solution, limits, test results and deployed version.
