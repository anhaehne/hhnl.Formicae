# Kubernetes capability bundles — implementation plan

Status: **Spec saved; runtime implementation planned**. Authorization: user “Go ahaed” to the next step of modeling DinD and one operator-backed companion before fixing implementation scope. Baseline: `kubernetes-capability-bundles`, 2026-10-10. This release publishes design documentation only; the tasks below describe future implementation.

## Task 1: Save spec documentation

Save `plan.md`, `shape.md`, `standards.md`, `references.md` and `contracts.md` in this folder. Align the baseline, roadmap and environment documentation. No supplied visuals; no visuals directory required. Record the sub-agent review, compatibility constraints and acceptance traces before implementation. **Completed by this documentation change.**

## Task 2: Implement bundle contracts, snapshots and composition

Add an administrator-managed catalog with immutable revisions, concurrency checks, archive/history and typed envelope validation over native Kubernetes JSON. Use the installed Kubernetes .NET client and API discovery; verify GenericClient/custom-object APIs against the installed package version before adding dependencies. Validate bounded parameter schemas and structural bindings without executable templating. Keep template content revision, envelope schema and renderer semantics independently versioned.

Extend environment selection and save-time resolution to embed complete trusted definitions, parameters and renderer version. Preserve schema-1 Default/old snapshots; introduce additive fields only with explicit serialization/size-limit review. Extend `EnvironmentDefinitions.ConfigurationEquivalent` to compare every new executable field and retain ordering only where semantically significant. Reject conflicting pinned definitions and unsupported renderer versions before launch. Retained versions never resolve against the live catalog.

Implement `capability:<name>` for configured infrastructure bundles, null inheritance and explicit `[]`. Scripts can select infrastructure bundles with matching task-kind metadata; browser/MCP remain agent-specific. Keep `nested-containers` as a compatibility alias to the built-in Docker implementation; reject duplicate alias/bundle selection. Preserve old script/default semantics; new infrastructure defaults require explicit environment configuration.

Compile selected bundles into an immutable execution plan with deterministic attempt-scoped names and content hash. Contributions include ordered init/native-sidecar containers, volumes, worker env/mount bindings, claimed pod settings and companion resource trees. Reject overlapping claims/collisions, dependency cycles and protected platform fields. Convert existing DinD to a built-in bundle without changing its socket/storage/probe/privilege behavior. Newly saved versions freeze the resolved built-in configuration; older versions use the documented legacy adapter.

## Task 3: Implement durable provisioning and cleanup

Model PostgreSQL-backed attempt/resource ledger entities; generate EF migrations using the standard. Persist desired identities and hash before Kubernetes operations, actual UID afterward, and reconcile uncertain outcomes using GET plus ownership/hash checks. Never attach to arbitrary objects solely because creation returned 409. Reuse verified objects on replay; allocate fresh identities for new attempts and loop visits. Prevent concurrent reconcilers from creating conflicting plans using existing optimistic-concurrency patterns.

Reconcile Provisioning, Waiting, Running, Capturing, CleanupPending and Cleaned phases. Evaluate explicit dependency/readiness/failure rules with a bounded selector language; compare observed generation where the resource supports it. Charge provisioning and waits to a durable absolute attempt deadline. Reduce the worker budget by elapsed provisioning time rather than resetting the deadline at Job creation.

For initial deterministic name/DNS bindings, create the final composed worker Job suspended to acquire its UID, create owned companions, await their declared prerequisites, then unsuspend. A suspended Job requires explicit Job patch permission and an application deadline because its active deadline does not cover pre-start waits. Never gate worker startup on endpoints that require that same worker to run. Status-derived worker bindings are deferred; if later added, create a durable ownership anchor before provisioning and create the final immutable Job only after resolving them.

Capture diagnostics before cleanup; delete attempt-owned companions on success, failure, cancel, deadline and failed provisioning, independently of `DeleteFinishedJobs`. Reconcile after service restart and terminal workflow completion. Verify UID on deletion, preserve external references, bound finalizer waits and surface persistent CleanupPending without falsely reporting cleanup success or stripping unknown finalizers. Keep execution outcome and cleanup outcome separate. Explicitly verify operator-managed descendants/storage under the bundle's cleanup contract.

## Task 4: Implement management and investigation

Provide administrator bundle authoring/history with native fragment editor, envelope fields, validation errors and composition preview. Environment configuration selects bundles, parameters and opt-in inherited defaults; steps inherit or explicitly select them. Show pinned revision, compatibility, privilege/secret requirements and conflicts before save/run.

Expose per-resource lifecycle/readiness/failure/cleanup evidence and separate worker/init/sidecar logs, bounded and redacted. Preserve script stdout/output semantics by keeping sidecar logs out of script output. Record selected bundle hashes, renderer, resource identities and sanitized status facts, never secret data or arbitrary credential-bearing custom-resource status.

## Task 5: Validate and release the implementation

Exercise the two design traces in `contracts.md` before final implementation scope is accepted. Add meaningful tests for snapshot trust/equality, null/empty behavior, script compatibility, collisions/protected fields, secret consumers, uncertain create responses, foreign 409s, concurrency, fresh retry versus replay, absolute deadlines and cleanup recovery. Add browser coverage for authoring/selection, version round trips, validation, requirements and lifecycle evidence.

Run targeted .NET filters and full fast validation (`dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --configuration Release`, frontend `npm run build`, Helm lint/render). Use `./scripts/formicae-dev.sh prepare`, `start`, `status`, `logs`, `stop`; inspect runtime/UI changes through Playwright MCP and run frontend `npm run test:smoke`. Run `./scripts/run-k8s-e2e.sh` with actual native DinD and a pinned test operator/custom resource in the disposable cluster. Validate resource/credential isolation, readiness, worker result, replay/restart, cancellation and absence of owned descendants after cleanup. Operator installation is an explicit E2E fixture prerequisite, never application behavior. Clean up preserved clusters.

Before publishing, update from the latest origin/main, review incoming changes, run relevant checks, increase a minor release version once and align props/chart/values/deployment docs. Push to main and verify Test → Build container images → Deploy Formicae for the released commit and deployed health. Report exact commands/outcomes and tests added/removed/edited. Current documentation release adds/removes/edits **0 / 0 / 0** tests; it does not claim future acceptance tests have run.
