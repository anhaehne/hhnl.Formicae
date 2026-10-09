# Workflow editor groups

Approved baseline: `workflow-editor-groups`, product owner, 2026-10-09, conversation response “Approved”.

1. Save spec documentation: scope, standards and code references.
2. Extend editor metadata with named groups and preset colors; retain absolute member positions and immutable version metadata.
3. Render React Flow parent containers, translate coordinates at the canvas boundary, and provide grouping, membership and ungrouping controls with undo/redo.
4. Verify persistence and runtime isolation in .NET and group interaction in browser tests; build and run the repository smoke suite.
5. Merge latest origin/main, increase the minor release version, push main and verify deployment.

## Verification

- `dotnet build hhnl.Formicae.slnx --no-restore --no-incremental`: passed, no warnings or errors.
- `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-build --no-restore`: all 1,144 tests passed on merged 0.28.0.
- `npm --prefix src/hhnl.Formicae.Api/ClientApp run build`: passed. Existing bundle-size advisory remains.
- `npm run test:smoke -- --config playwright.local.config.ts --workers=1` from ClientApp: all 96 tests passed in 7.2 minutes. Temporary local config reused the reserved development harness and installed Chromium 1243; repository Playwright configuration is unchanged. The initial six-worker run timed out in existing tests; the complete single-worker run passed every case.
- `helm lint deploy/helm/formicae` and `helm template formicae deploy/helm/formicae --namespace formicae`: passed; rendered API/worker images and chart use 0.28.0.

Development harness: `./scripts/formicae-dev.sh prepare`, `start`, `status`, `logs`, `stop`. Preparation built/restored .NET and installed npm dependencies; browser download was stopped when it stalled, and validation used the existing Chromium through a temporary local config.

Live Playwright MCP inspection reproduced saved groups and captured canvas/inspector screenshots, accessibility snapshots, console/network records and a session under `test-results/groups-mcp/`. No console errors/warnings; inspected API requests returned 200. Saved group screenshot: `visuals/workflow-groups.png`.

Tests added: **3** (1 .NET, 2 browser). Tests removed: **0**. Existing tests edited: **0**.

Release integration: merged main commit `c0b6b9a` (0.27.0) intentionally, preserving port layout, compact management, selected-definition Manual Start and the updated baseline documentation policy. Release version: 0.28.0. Push waits for the preceding release's explicit deployment verification and release handoff.

CI follow-up: the member-drag assertion initially treated a responsive header shift as node movement. The longer Unsaved status wraps Save Version on the CI font metrics, shifting the canvas down 40 pixels. Compare member offsets relative to a sibling and assert its absolute flow coordinates are unchanged; group movement behavior remains the approved scope. This correction stays within the unreleased 0.28.0 feature release.

Corrected member regression verified with `npx playwright test --config playwright.local.config.ts --workers=1 tests/e2e/groups.local.spec.ts --grep "group members"`: 1 passed. Temporary copied test and local config used isolated ports 5011/5181, installed Chromium and a 60-second local timeout while another workspace used the shared test ports. Temporary files were removed; application and repository browser configuration remain unchanged. CI repeats the full standard suite for the corrected release commit.


## Group drag flicker patch (0.29.1)

User request, 2026-10-09: “Groups flicker when moved”. Baseline revision `workflow-group-drag-flicker` documents stable rendering throughout repeated drags. Merged the latest 0.29.0 main release and preserved typed variables and the prior layout. Controlled group nodes omitted the measured dimensions already recorded by `onNodesChange`; React Flow hid each rebuilt container until it measured it again. Preserve those dimensions just as task nodes do, while keeping CSS sizing and native parent dragging. See [React Flow Node dimensions](https://reactflow.dev/api-reference/types/node).

The new browser regression observes every hidden-style transition during repeated drags, including transitions that disappear before the observer runs. It failed before the fix with 96 hidden transitions, and passes with zero afterward. Tests added: **1 browser**; removed: **0**; existing tests edited: **0**.

Verification:

- `npm run test:smoke -- --config playwright.local.config.ts --workers=1 tests/e2e/editor.spec.ts --grep 'group'`: all 3 group tests passed.
- `npm run test:smoke -- --config playwright.local.config.ts --workers=1`: 98 passed, one existing navigation test timed out waiting for Definitions during concurrent builds. `npm run test:smoke -- --config playwright.local.config.ts --workers=1 tests/e2e/editor.spec.ts --grep 'refresh keeps draft'`: the remaining case passed in 4 seconds. Temporary config used installed Chromium 1243 and the development harness; it was removed before commit.
- `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --configuration Release`: 1,172 passed.
- `npm --prefix src/hhnl.Formicae.Api/ClientApp run build`: passed; existing bundle-size advisory remains.
- `helm lint deploy/helm/formicae` and `helm template formicae deploy/helm/formicae --namespace formicae`: passed, matching 0.29.1 chart/API/worker images.

Reused the development harness prepared earlier in this task, with `start`, `status`, `logs` and `stop`. Live Playwright MCP inspection confirmed a drag with zero hidden transitions and no console errors or warnings; inspected API requests returned 200. Screenshot, snapshot, network and session evidence is under ignored `test-results/group-flicker-mcp/`. Automated CI repeats the standard full browser suite and Kubernetes E2E before image publishing and exact-image/health deployment verification.
