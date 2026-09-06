# Custom task references

Source inspection: 2026-09-06. No application files changed during this planning task.

- [Issue #15](https://github.com/anhaehne/hhnl.Formicae/issues/15): reusable custom definitions, validated inputs and persisted task output.
- `agent-os/specs/2026-09-06-1445-task-issues/plan.md`: original dependency plan; avoid mapping custom behavior onto Implement.
- `agent-os/specs/2026-09-06-1700-personas/plan.md`: reusable catalog, optimistic revision conflicts, immutable snapshots and delayed-save behavior.
- `agent-os/product/mission.md`, `roadmap.md`, `tech-stack.md`: task-specific agents, configurable behavior, existing CLI harness, PostgreSQL and Kubernetes.
- `src/hhnl.Formicae.Worker/Program.cs`: `RequiresRepositoryCheckout` allows Plan/Implement/AddressComments; `CanCommitRepositoryChanges` only Implement/AddressComments. Non-checkout OpenHands currently receives null working-directory override. Custom must preserve credential/commit gating and explicitly use scratch cwd. `WorkerDeadlinePolicy.From` is tied to commit-capable tasks, so Custom needs ordinary hard timeout with no checkpoint commits.
- `src/hhnl.Formicae.Infrastructure/OpenHands/OpenHandsAgentRunner.cs`: AI resolution, Git token allowlist, task-dependent browser/DinD/deadline requirements, durable job names, uncertainty wrapping and final agent-output extraction. Extend generic execution without granting existing implementation-only requirements.
- `src/hhnl.Formicae.Infrastructure/RuntimeJob.cs`: runtime-neutral context files, deadline policy and ReuseExisting flag already exist.
- `src/hhnl.Formicae.Application/Workflows/WorkflowOrchestrator.cs`: built-in dispatch, status/step mapping, common persona preparation, completion and cursor helpers. Custom requires dedicated dispatch and must not inherit Plan label gates or built-in provider side effects.
- `src/hhnl.Formicae.Application/Workflows/WorkflowService.cs`: explicit retry state switches and per-node/loop retry restrictions; append Custom mappings and preserve prepared inputs across attempt renewal.
- `src/hhnl.Formicae.Application/Workflows/WorkerAgentMessageService.cs`: latest-kind lookup and permissive blank-ExternalId behavior create a concrete callback identity hazard for repeated generic kinds.
- `src/hhnl.Formicae.Application/Workflows/WorkerAgentAuthRefreshService.cs`: latest-kind lookup also misses valid callbacks from another same-kind execution; exact external identity is required.
- `src/hhnl.Formicae.Application/Workflows/WorkflowDefinitionValidator.cs`, `WorkflowNodeDefinitions.cs`, `WorkflowParallelDefinitions.cs`, `WorkflowDecisionDefinitions.cs`: built-in mapping, sequential loop body rules, Plan-only parallel branches and output-source dominators. Update generic recognition without widening parallel scope.
- `src/hhnl.Formicae.Application/Workflows/PersonaDefinitions.cs`, `PersonaPromptComposer.cs`: authoritative save-time resolution and pinned runtime guidance. Extend AI eligibility for Custom and compose guidance exactly once.
- `src/hhnl.Formicae.Application/Workflows/WorkflowModels.cs`: existing enums, per-node TaskRun identity, Output and ExecutionAttemptId. The 60,000-character helper here bounds provider comment formatting only; it is not a general stored task-output limit.
- `src/hhnl.Formicae.Infrastructure/Persistence/FormicaeDbContext.cs`: enums are string-converted; task-run uniqueness uses workflow/node/loop iteration. New enum members alone need no DB conversion; catalog and execution JSON column do.
- Persona service/store/API/UI files provide catalog and revision-concurrency patterns. Reuse those patterns rather than inventing a second permissions or conflict mechanism.

No new external SDK or expression/template library is required. Existing argument-list process APIs and runtime abstractions remain the supported integration path. Consult current official CLI documentation if implementation changes CLI invocation syntax.
