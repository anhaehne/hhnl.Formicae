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
