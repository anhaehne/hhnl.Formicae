# Verification

Version: 0.24.0. Tests: 15 cases added (13 backend and 2 browser), 2 existing signed-delivery cases edited, 0 removed.

## Backend

- `dotnet restore tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --disable-parallel -m:1` passed. Default parallel restore failed without usable diagnostics in this worker.
- `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore -m:1 -v minimal` passed: 1,078 cases, zero failures/skips. This run preceded the final loop-entry binding regression addition.
- Final focused command: `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore -m:1 --filter 'FullyQualifiedName~IssueCommentTaskTests|FullyQualifiedName~TaskDataPassingTests|FullyQualifiedName~WorkflowEventApiTests' -v minimal` passed: 43 cases, zero failures/skips (including the final loop-entry regression).

The .NET runner needs a local socket; test execution used the reviewed sandbox escalation after the sandbox denied socket creation.

## Frontend and runtime

- `npm ci --no-audit --no-fund` passed in ClientApp.
- `npm run build` passed; generated static assets are included. Vite reports its existing large-chunk warning.
- `npm run test:smoke -- --config=/tmp/formicae-issue-comments.playwright.config.ts issue-comments.spec.ts` passed: both new cases.
- Final focused browser command: `npm run test:smoke -- --config=/tmp/formicae-issue-comments.playwright.config.ts issue-comments.spec.ts starts.spec.ts extensions.spec.ts` passed: 14 cases, zero failures.
- `node /tmp/formicae-issue-comment-review.cjs` passed: both output ports, correct event binding, multiline text, no model controls, no console errors, all observed API responses HTTP 200. Screenshot: test-results/issue-comment-review.png; network/console evidence: test-results/issue-comment-browser-review.json.

The configured app browser host disconnected, so inspection used local Playwright with installed Chromium. Default ports 5000/5173 belong to another worktree and were left intact. Temporary copies of smoke tests/config changed only loopback ports to 5100/5273, used the prebuilt API from its correct content root, and selected installed Chromium. Repository test configuration is unchanged.

Full `npm run test:smoke -- --config=/tmp/formicae-issue-comments.playwright.config.ts` was attempted. An initial setup missed development configuration; after correcting it, one pre-existing editor case hit its timeout and the run was interrupted with exit 143. A successful full smoke result is not claimed.

`./scripts/formicae-dev.sh prepare` was attempted but default restore failed. Preparation was completed with the restore/build/test and npm commands above. `./scripts/formicae-dev.sh start` and `status` identified the other worktree's shared services; verification used isolated managed servers instead of stopping them.

## Kubernetes

`./scripts/run-k8s-e2e.sh` failed during fixture setup: `kind load image-archive /tmp/formicae-e2e/formicae-worker-e2e.tar --name formicae-e2e` timed out. One manifest test passed; all 13 deployment cases failed from the shared fixture error before application behavior ran. The fixture removed its temporary cluster; `kind get clusters` confirmed only the other worktree's cluster remained. No Kubernetes success is claimed.

`git diff --check` passed.

Cleanup: stopped only the isolated API/Vite processes on ports 5100/5273 after verifying their command lines identify this worktree. Other worktree services were preserved.
