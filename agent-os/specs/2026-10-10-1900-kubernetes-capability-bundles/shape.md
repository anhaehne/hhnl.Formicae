# Kubernetes capability bundles — shaping notes

## Scope and authorization

User request: “Think about how we can expand capabilities to allow for a docker sidecar. This is not specific to docker but should provide anything kubernetes that is outside of the container image. Discuss with a sub agent.” User “Go ahaed” approves the proposed next step: save a spec modeling DinD and an operator-backed companion before fixing implementation scope. Current work is design documentation; runtime implementation remains planned.

Kubernetes bundles extend an execution environment independently of its worker image. Cover pod-local sidecars/init containers/volumes, worker bindings, scheduling/runtime-class and other explicitly claimed pod settings, plus namespaced companion and custom-resource instances. An administrator must permit each resource kind and its lifecycle. Broad Kubernetes vocabulary is supported by native fragments; permissions and ownership define which instances Formicae can provision.

## Decisions

Use administrator-authored native Kubernetes trees inside a typed Formicae envelope for parameters, bindings, dependencies, readiness, lifecycle, runtime/task compatibility and policy. Avoid a second partial Kubernetes schema or arbitrary whole-Job patches. Preserve platform worker protocol and execution controls.

Catalog content revisions are separate from envelope schema and renderer semantics. Saved workflow versions contain executable content rather than only mutable catalog references. API round trips, equality validation, size limits and historical reads must cover the complete snapshot.

A durable per-attempt ledger is mandatory even when Kubernetes owner references exist. Owner references assist garbage collection; they do not solve uncertain API responses, retained Jobs, finalizers or external operator side effects. Cleanup continues after workflow terminal state and has independent visible evidence.

Use deterministic initial bindings: stable resource names, service DNS, secret name/key references and shared mounts. Defer arbitrary status-derived worker values to avoid immutable Job-template changes and accidental credential capture. Readiness is explicit, bounded and dependency-aware; no generic custom-resource Ready convention.

Security is a deployment policy as well as an authoring permission: declared secret consumers, constrained parameters, resource-kind/API discovery, namespace-scoped RBAC, privilege approval by operator configuration and admission controls. A privileged DinD built-in remains an explicit exception. Custom resources can cause cloud/storage side effects; approving their GVK requires a tested cleanup contract.

## Initial implementation boundary

The proposed first runtime release supports the complete envelope/compiler/ledger path, native sidecars and init containers, volumes/worker bindings, claimed pod fields and allowlisted namespaced companions/custom resources. Validate built-in Docker and a single-instance operator-backed PostgreSQL example. This proves generic resources through the same path; it does not create a database-specific application integration.

Cluster-scoped resource creation, installing/upgrading operators or CRDs, shared resources spanning attempts, arbitrary runtime scripts in the control plane, status-derived worker values and non-Kubernetes translations are deferred. Referencing approved pre-existing cluster configuration such as a RuntimeClass remains possible; Formicae does not own it. New Kubernetes bundles fail clearly on Docker/Podman execution.

## Context and references

No mockups or screenshots supplied. Extend existing environment/history, workflow inspector and execution-investigation UI patterns. Product alignment: self-hosted ephemeral Kubernetes execution, configurable environments, immutable workflow versions, selected secrets and durable cancel/retry behavior. Product mission and core stack remain unchanged.

Paseo sub-agent `3a0540f1-3d30-4f1c-8829-e96ec88adda3` reviewed the design read-only. Its refinements are included: complete snapshot equality, no last-wins composition, ledger even with suspended Job ownership, retained-Job cleanup, explicit CR readiness, script support and admission/RBAC bounds. No additional sub-agent implementation work is authorized by this document.

## Standards applied

`database/ef-migrations`: generated migrations for the proposed resource ledger; no migration or database model is changed by the current spec. Full standard text is in `standards.md`.
