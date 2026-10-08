# Verification

Version: 0.21.0. Added 14 test cases (13 .NET graph cases and one browser case), edited one existing browser case to check exclusive control connections, removed none.

| Command | Outcome |
| --- | --- |
| `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --verbosity minimal -m:1 -p:UseSharedCompilation=false` | 991 passed, zero failed. |
| `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --filter FullyQualifiedName~WorkflowGraphTests --verbosity minimal -m:1 -p:UseSharedCompilation=false` | Final graph regression run: 13 passed, zero failed, including final planning output retention. |
| `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --filter FullyQualifiedName~WorkflowParallel --verbosity minimal -m:1 -p:UseSharedCompilation=false` | 56 existing parallel cases passed. |
| `npm run build` (ClientApp) | Passed; production assets regenerated. Existing large-chunk advisory remains. |
| `PLAYWRIGHT_BROWSERS_PATH=/workspace/hhnl.Formicae/test-results/browsers npm run test:smoke -- --workers=2` (ClientApp) | All 76 passed, including saved graph execution through the running API and join start times after both branch completions. |
| `helm lint deploy/helm/formicae` | Passed. |
| `git diff --check` | Passed. |
| `FORMICAE_E2E_KEEP_CLUSTER=true ./scripts/run-k8s-e2e.sh` | Final run on 2026-10-08: all 13 passed, zero failed or skipped, duration 7 minutes 39 seconds. Output retained in `test-results/kubernetes-e2e.log`. The preserved test cluster was deleted after verification. |

## Kubernetes cluster recovery

The old kind test cluster had no CNI daemonset and its node was NotReady. Recreated only the isolated `formicae-e2e` cluster using:

```bash
kind delete cluster --name formicae-e2e --kubeconfig /tmp/formicae-e2e/kubeconfig
kind create cluster --name formicae-e2e --kubeconfig /tmp/formicae-e2e/kubeconfig --wait 5m
FORMICAE_E2E_KEEP_CLUSTER=true ./scripts/run-k8s-e2e.sh
kind delete cluster --name formicae-e2e --kubeconfig /tmp/formicae-e2e/kubeconfig
```

Kind installed its CNI successfully; the node, PostgreSQL and API became healthy. All runtime, deadline, cancellation, migration, restart persistence and diagnostics tests passed. The E2E overlay alone uses temporary PostgreSQL storage; the production base retains its persistent-volume claim. No Kubernetes test cases were added, removed or edited during the cluster recovery.

## Development harness and browser evidence

Ran `./scripts/formicae-dev.sh prepare`, then `start`, `status`, `logs`, and `stop`. Restore and build passed. Prepare's browser installation failed because `/ms-playwright` is read-only and its existing revision differs from the project revision. Installed the matching browser with `PLAYWRIGHT_BROWSERS_PATH=/workspace/hhnl.Formicae/test-results/browsers npx playwright install chromium`. An interruption cleared the earlier `/tmp` install, so final verification uses the ignored repository test-results directory.

The development API and UI were healthy. A standalone Playwright inspection used an actual output-to-input mouse drag, preserved all four diamond graph edges, saved the version, and observed 67 network responses with zero failed requests and zero console errors. Artifacts: `test-results/dev/task-graph-port-drag.png` and `test-results/dev/task-graph-port-drag.zip`. The smoke screenshot is `test-results/playwright/smoke-ordinary-outputs-pre-4e42b-ins-when-saved-and-reloaded-chromium/task-graph.png`.

The browser MCP connector reported that no automation host was connected; direct Playwright supplied page, console, network, screenshot and trace inspection instead. Local API/UI processes were stopped after verification.

## Scope

Ordinary task graphs support nested branches, all-input joins, multiple terminal tasks, pause, restart, retry, named input bindings and single-entry triggers. Mixing implicit task graphs with loop, decision or explicit parallel control nodes is rejected by validation and remains future work. Existing sequential/control workflows retain their execution semantics.
