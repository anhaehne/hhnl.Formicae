# Implementation verification

Implemented on `codex/workflow-control-nodes`, version 0.10.0.

## Results

- `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --configuration Release -v quiet`: 289 passed, 0 failed or skipped. PostgreSQL test containers used the existing local Podman engine with `DOCKER_HOST=npipe://./pipe/podman-machine-default`.
- From `src/hhnl.Formicae.Api/ClientApp`, `npm run build`: TypeScript and production Vite build passed; tracked static assets regenerated.
- From the same directory, `npm run test:smoke -- --trace on`: 6 passed. Browser coverage includes creating, configuring, connecting, saving, reloading and deleting control nodes; legacy loop and trigger conversion; immutable older versions; existing CLI model selection. The new-node test checks browser console/page errors and captures a full-page screenshot.
- `FORMICAE_CONTAINER_CLI=podman bash scripts/run-k8s-e2e.sh`: 5 passed. The isolated kind cluster exercises v1alpha3 loop execution, task iteration identity, persistence after API restart, old database history and failed-rollout diagnostics. The fixture cleans up its test cluster.
- `helm lint deploy/helm/formicae`: passed.
- `podman build -f src/hhnl.Formicae.Worker/Dockerfile -t localhost/formicae-worker:0.10.0 .`: passed.
- In a disposable container using that worker image, `bash scripts/formicae-dev.sh prepare`, `start`, `status`, `logs`, and `stop`: passed. API health and UI returned HTTP 200; both processes stopped. Source was copied into the container; shell script line endings were normalized in the test archive only.
- `git diff --check`: passed.

Test changes: 22 added cases (21 backend and 1 browser), 2 edited tests (legacy browser conversion and Kubernetes workflow execution), 0 removed.

## Findings resolved

Creating a new definition previously caused the default definition to be reselected by the selection effect. Explicit draft state now preserves the new definition. At intermediate window widths the three-panel editor clipped graph nodes; the responsive layout and lower graph zoom limit keep them accessible. Both fixes were reproduced through browser tests.

The editor uses native React Flow custom nodes and named handles. Playwright MCP was unavailable in this session, so the repository Playwright runner provided real browser execution, console checks, traces and screenshots; the resulting editor screenshot was visually inspected.

## Compatibility and scope

Persisted v1alpha1/v1alpha2 versions are not rewritten. Editing converts a draft and saves a new v1alpha3 version. Task IDs and AI settings survive conversion. Runtime normalization retains the existing loop iteration, guard, retry and recovery mechanisms; control nodes do not launch agent jobs. No database migration is required.

This change has not been committed, pushed or deployed. Validation used local disposable containers and the isolated E2E cluster, not the live deployment.
