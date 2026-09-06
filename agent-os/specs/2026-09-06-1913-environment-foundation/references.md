# Environment foundation references

- [Current issue #19](https://github.com/anhaehne/hhnl.Formicae/issues/19), read with `gh issue view 19 --json number,title,body,url` on 2026-09-06: reusable creation/selection, validation before use and existing defaults.
- `agent-os/specs/2026-09-06-1445-environment-issues/plan.md`: original family dependencies and security boundaries.
- `agent-os/specs/2026-09-06-1700-personas/plan.md`: catalog optimistic concurrency, server snapshots, deleted-reference behavior and delayed-save protection.
- `agent-os/specs/2026-09-06-1857-custom-tasks/plan.md`: reviewed Custom agent scratch runner, independent worker deadline and pinned prepared execution inputs. #19 reuses its deadline mechanism when that delivery is available.
- `src/hhnl.Formicae.Infrastructure/RuntimeJob.cs`: image, environment, command, context/secret files, execution requirements/policy and durable reuse already share one runtime-neutral specification.
- `src/hhnl.Formicae.Infrastructure/OpenHands/OpenHandsAgentRunner.cs`: existing task-dependent requirements, implementation timeouts, global worker image and restricted repository-token creation. Map environment fields here without duplicating launch orchestration.
- `src/hhnl.Formicae.Infrastructure/Kubernetes/KubernetesJobRunner.cs`: `ResolveExecutionPolicy` uses explicit task policy or runtime default and clamps checkpoint grace. Job construction already controls image, resources, scoped secrets and optional DinD; these remain platform controls in #19.
- `src/hhnl.Formicae.Infrastructure/Containers/ContainerJobRuntime.cs`: equivalent policy resolution and timeout polling; explicit worker propagation is needed when an environment caps a previously implicit policy.
- `src/hhnl.Formicae.Worker/Program.cs`: commit-capable checkpoint logic is distinct from a plain non-checkpoint deadline. Never enable commit/browser/DinD behavior just because an environment is selected.
- `src/hhnl.Formicae.Application/Workflows/PersonaDefinitions.cs`, `PersonaService.cs`: authoritative snapshot resolution, pinned runtime validation and immutable default patterns.
- `src/hhnl.Formicae.Application/Workflows/WorkflowDefinitionService.cs`: save enrichment exactly once and separate current-catalog versus pinned-runtime validation.
- `src/hhnl.Formicae.Application/Workflows/WorkflowOrchestrator.cs`, `WorkflowOrchestrator.Parallel.cs`: both sequential and direct parallel launches need the common preparation contract.
- `agent-os/standards/database/ef-migrations.md`: generate migrations with EF tooling; never hand-write migrations or snapshots.

No new library or CLI integration API is needed for planning this foundation. Later image/MCP/tool features must consult current primary documentation for their actual execution integration.
