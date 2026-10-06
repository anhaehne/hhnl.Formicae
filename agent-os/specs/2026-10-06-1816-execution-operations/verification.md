# Execution operations verification

## Release and test changes

Version 0.19.0 is aligned across project, Helm, Kubernetes worker image and deployment documentation. This branch is stacked on the repaired PR #67 commit `f933399`.

Tests added: 52 declarations / 64 cases (20 backend declarations / 30 cases, 18 worker/runtime declarations / 20 cases, 3 Kubernetes tests, 11 browser tests). Tests removed: 0. Existing test declarations edited: 15 (6 runtime, 2 historical migration/catalog, 1 Kubernetes deadline, 6 browser). Two additional fixture helpers were adjusted: historical migration setup and authorization-host scheduling isolation.

## Final commands and outcomes

```sh
dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --configuration Release --no-restore -m:1 --logger 'trx;LogFileName=execution-operations-backend.trx' --results-directory .artifacts/execution-operations
```

Passed 888/888, no skipped tests. Covers PostgreSQL cursor commit ordering, migration backfill, retry archives, callback deduplication/stale rejection, authorization, export sanitization, pause/cancel/restart and runtime lifecycle. Earlier historical-schema fixture failures and scheduler contention in the authorization fixture were repaired; final full run is green.

```sh
cd src/hhnl.Formicae.Api/ClientApp
npm run build
PLAYWRIGHT_BROWSERS_PATH=/workspace/hhnl.Formicae/.artifacts/playwright-browsers npm run test:smoke -- --workers=1
```

Production build passed, including TypeScript. Vite reports the existing large bundle warning for ELK/application assets. Full browser suite passed 69/69 in 3.2 minutes, including all 11 new investigation tests. Existing history assertions were migrated to the new inspector and readiness assertions account for long-lived SSE.

```sh
./scripts/run-k8s-e2e.sh
FORMICAE_E2E_KEEP_CLUSTER=true dotnet test tests/hhnl.Formicae.KubernetesE2ETests/hhnl.Formicae.KubernetesE2ETests.csproj --no-build --no-restore --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=execution-operations-k8s.trx' --results-directory .artifacts/execution-operations
kind delete cluster --name formicae-e2e --kubeconfig /tmp/formicae-e2e/kubeconfig
```

The initial script run lost its Kubernetes API connection mid-suite. The final detailed run passed 9/9, including real live job logs, retention until acknowledgment, idempotent cancellation/termination and PostgreSQL upgrade/SSE replay across API restart. The preserved test cluster was deleted. Subsequent changes affect audit metadata and frontend only; job runtime and migration code remain the verified versions.

```sh
docker build --file src/hhnl.Formicae.Worker/Dockerfile --tag formicae-worker:execution-operations-test .
docker run --rm --entrypoint /bin/sh formicae-worker:execution-operations-test -lc 'dotnet --version && node --version && docker --version && kubectl version --client --output=yaml && kind version && playwright --version && playwright-mcp --version && test -d /ms-playwright/chromium-1228 && test -d /ms-playwright/chromium-1243'
docker build --file src/hhnl.Formicae.Api/Dockerfile --tag formicae-api:execution-operations-test .
helm lint deploy/helm/formicae
helm template formicae deploy/helm/formicae --namespace formicae --output-dir .artifacts/execution-operations/helm
git diff --check
```

Both images, packaged worker tools, Helm lint/render and whitespace checks passed. Final API image is `2251f760f516`; worker image is `5ffdba6e7e33`.

## Running application and native Playwright MCP

```sh
./scripts/formicae-dev.sh prepare
./scripts/formicae-dev.sh start
./scripts/formicae-dev.sh status
./scripts/formicae-dev.sh logs
node .artifacts/execution-operations/mcp-inspect.mjs
./scripts/formicae-dev.sh stop
```

Preparation restored/built backend and installed frontend dependencies. Its browser installer selected an unexpected alpha revision and was replaced by the pinned installed Playwright CLI Chromium install. Final start/status reported healthy API/UI; logs were inspected; both processes were stopped before full browser testing.

Native local Playwright MCP inspected a real fake-adapter default workflow without browser API mocks: four default nodes automatically render and fit within the graph; selected attempt logs contain its output; resolved settings contain its attempt ID; native SSE returns 200; console has zero errors and zero warnings. This inspection exposed and fixed slow-layout cancellation and missing controlled ReactFlow measurements. The regression test deliberately delays layout across status polling and verifies graph fitting plus viewport preservation.

Local review artifacts: `.artifacts/execution-operations/browser/live-execution.png`, `live-execution-snapshot.md`, `live-execution-trace.zip`, `console.txt`, `network.txt`; `native-attempt-evidence.json`; backend and Kubernetes TRX files in `.artifacts/execution-operations/`. Browser smoke artifacts include native streaming screenshot and trace. Artifacts are ignored and excluded from Docker context.

## Acceptance coverage

Durable log identity/delivery/cursor/export contracts are covered by `WorkerLogDeliveryTests`, `WorkflowExecutionTests`, `WorkflowExecutionPersistenceTests`, callback tests and real-job E2E. Runtime evidence-before-cleanup and cancellation/pause/restart semantics are covered by `RuntimeLifecycleTests`, `WorkflowRuntimeControlTests`, custom/parallel orchestrator suites and Kubernetes E2E. Browser `execution.spec.ts` covers pinned graph/attempt evidence, filters/history, reconnect/deduplication, saved views, failure navigation, viewer permissions, controls, deep links, refresh errors and structural nodes. Historical custom producer/consumer and environment profile tests retain provenance and scope assertions.

All release acceptance items are implemented. Automatic retry/backoff, replay/reset of side effects, changed-definition execution, schedules, notifications, artifact stores, subworkflows, compensation and distributed quotas remain the documented separate roadmap. Existing GitHub issues for environment tools, images, MCP, secrets, capabilities and scripts remain open.
