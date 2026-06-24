# Standards: MVP Vertical Slice

## 1) Scope Discipline
- Implement only the manual trigger path and required vertical-flow components.
- Do not add non-requested integrations or alternate triggers in this slice.

## 2) Determinism Standard
- All new tests for core workflow behavior must be deterministic.
- No live GitHub/Azure DevOps calls from tests; use fake adapters by default.

## 3) Persistence Standard
- All run lifecycle transitions must be persisted in PostgreSQL.
- Record request metadata and final status in a queryable model to enable replay/debug.

## 4) Execution Standard
- OpenHands runner must run in headless mode in the MVP slice.
- CLI invocation parameters must be explicit and documented in the slice code.

## 5) Workflow Standard
- Development sequence for this slice is:
  1. Plan (spec and acceptance defined)
  2. Implement (minimal end-to-end path)
  3. Draft PR (ready for review with known open risks)

## 6) Kubernetes Standard
- Prefer Kubernetes-native constructs over ad-hoc local simulation for runtime behavior.
- Deployment assumptions and names should be explicit and minimal for local reproducibility.
