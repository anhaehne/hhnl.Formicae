# Implementation plan

1. Save spec documentation and record baseline approval.
2. Add definition contracts, variable validation, transitive binding checks and deterministic typed evaluation with frozen provenance.
3. Add compact variable nodes, typed connections, ordered source editing, persistence and execution evidence.
4. Verify aggregation, invalid graphs, loop/event restrictions, retry evidence and editor behavior with targeted tests, repository validation and running application/browser inspection.
5. Merge latest origin/main, increase the minor release version, align documentation, push main and verify automated deployment.

## Implementation and verification

Implemented typed data-only variables in release 0.29.0, including ordered sources, exact scalar types, Aggregate/First/Override, string separators, boolean Any/All, transitive control/event/loop validation, frozen recursive provenance, compact editor nodes and history inspection. Existing task inputs remain single-source. No database migration or worker protocol change. Merged main's 0.28.0 groups and 0.27.0 layout, then its test-only correction 75481e0.

Tests added: **30** (27 variable unit cases, one orchestration/retry regression and two browser cases); removed: **0**; existing tests edited: **2** (data-edge selectors). Incoming groups test correction is retained.

- `dotnet restore hhnl.Formicae.slnx` and `dotnet build hhnl.Formicae.slnx --no-restore`: passed.
- `dotnet test hhnl.Formicae.slnx --no-restore`: **1,172 passed**, zero failed/skipped. Final targeted `--filter 'FullyQualifiedName~WorkflowVariableTests'`: **27 passed**.
- `npm run build` from ClientApp: passed; existing large-bundle warning remains.
- `helm lint deploy/helm/formicae` and `helm template formicae deploy/helm/formicae`: passed. `git diff --check`: passed.
- `./scripts/formicae-dev.sh prepare`, `start`, `status`, `logs api`, `stop`: API/UI healthy. Prepare's shared browser-cache install was stopped after installing the matching Chromium separately with `PLAYWRIGHT_BROWSERS_PATH=/tmp/formicae-variable-browsers ./node_modules/.bin/playwright install chromium`.
- Live Playwright MCP inspected the seeded two-source variable, edited/saved its mode, checked zero control ports, console and successful network responses. Screenshot and browser evidence are under ignored `test-results/variable-mcp/`; console errors: zero.
- `PLAYWRIGHT_BROWSERS_PATH=/tmp/formicae-variable-browsers npm run test:smoke -- --workers=1`: **96 passed, two timed out** while full backend/build/Kubernetes checks also ran. Both new variable cases passed. The ordinary graph workflow completed successfully after its polling deadline. Targeted rerun `npm run test:smoke -- --workers=1 --grep 'ordinary outputs preserve|trigger and loop nodes|group members'`: **3 passed**, covering both timeouts and the merged groups correction.
- `./scripts/run-k8s-e2e.sh`: **32 passed**, zero failed/skipped, in 14m42s. Used an isolated temporary kubeconfig; automatic cluster cleanup completed.

Main push is queued until the coordinated 0.28.0 groups deployment is healthy.
