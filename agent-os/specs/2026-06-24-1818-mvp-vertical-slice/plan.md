# Plan: MVP Vertical Slice

## Objective
Deliver a minimal, verifiable end-to-end slice for form/task execution that is GitHub-first, Kubernetes-native, and deterministic for testing.

## Scope (In)
- GitHub-first trigger path: manually invoke pipeline via issue comment or manual workflow API call.
- Kubernetes-native deployment flow for a runnable minimal MVP on existing cluster primitives.
- OpenHands CLI integration to run agent tasks in headless mode.
- PostgreSQL-backed persistence for task/workflow state.
- Fake adapters for external services (GitHub/Azure DevOps) to keep MVP deterministic.
- Deterministic tests that validate the slice without live external dependencies.

## Scope (Out)
- Automatic issue triage and webhooks-based auto-triggering.
- Complex identity/authorization model beyond manual invocation.
- Production hardening, secret management parity, and multi-cloud portability.
- Non-MVP workflow integrations or advanced retry/recovery policies.

## Milestones
1. **Slice Plan (now complete)**
   - Define bounded use case and acceptance criteria in this spec set.
2. **Implementation**
   - Build minimal domain flow from manual trigger through worker execution and persistence.
3. **Validation**
   - Add deterministic tests for domain and adapter behaviors, including fake adapters.
4. **Pre-PR Hardening**
   - Basic docs and rollout checklist for a draft PR from MVP branch.

## Execution Sequence
1. Manual trigger command reaches API controller.
2. Controller persists run request in PostgreSQL.
3. OpenHands CLI runner starts headless and executes the planned task/workflow.
4. Worker updates state and artifacts; run result is recorded deterministically.
5. API reports completion state for user verification.

## Acceptance Criteria
- A single manually triggered request can start an MVP workflow execution.
- Run state is persisted in PostgreSQL with explicit terminal states.
- Fake adapters are used in tests to avoid external API flakiness.
- A deterministic test set demonstrates reproducibility of happy path and one failure mode.
- Slice is ready for draft PR with a static workflow plan → implement → draft PR sequence documented.
