# References

- [Issue #17](https://github.com/anhaehne/hhnl.Formicae/issues/17): per-step selection, persisted history and invalid-reference validation; read through GitHub CLI on 2026-09-06.
- `agent-os/specs/2026-09-06-1913-environment-foundation/`: approved catalog, workflow-default snapshot, runtime-cap and profile-only audit contracts.
- `src/hhnl.Formicae.Application/Workflows/EnvironmentDefinitions.cs`: extend ResolveAsync/ValidateRuntime/ResolveForTask instead of creating a second resolver.
- `EnvironmentModels.cs`, `EnvironmentService.cs`: existing immutable default and revisioned catalog; no API/catalog duplication needed.
- `PersonaDefinitions.cs`: per-task override/inheritance and cached distinct-reference resolution patterns.
- `WorkflowDefinitionService.cs`: server enrichment, enabled/disabled saving and nonpersisting current-reference validation.
- `WorkflowNodeDefinitions.cs`: normalization must retain node record fields and document default metadata.
- `WorkflowOrchestrator.cs`, `.Parallel.cs`, `.Custom.cs`: common agent preparation and existing exact-task profile settings events.
- `RuntimeJob.cs`, KubernetesJobRunner and ContainerJobRuntime: selected timeout cap is already mapped centrally; no new adapter setting needed.
- Frontend `workflowEditor/EnvironmentPicker.tsx`, `Inspector.tsx`, `state.ts`, `WorkflowDefinitionsPage.tsx`, `workflowGraph.ts`: selector reuse, saved baseline, delayed saves, graph serialization and inspector layout. Verify picker filename against current #19 implementation before editing.
- `EnvironmentDefinitionTests.cs`, `EnvironmentOrchestratorTests.cs`, `EnvironmentApiTests.cs`: default parity, pinned snapshots, profile audit and catalog behavior tests to extend.
- `agent-os/product/mission.md`, `roadmap.md`, `tech-stack.md`: operator-managed task-specific environments on the existing .NET/React/PostgreSQL/Kubernetes stack.
