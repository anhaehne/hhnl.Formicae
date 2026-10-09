# Workflow end node

1. Save approved requirements and spec documentation.
2. Add End validation, durable successful completion and cancellation of siblings/waits across sequential, graph and cycle scheduling.
3. Add the editor palette, terminal ports and inspector explanation; preserve saved versions and history.
4. Verify targeted regressions, repository tests, browser smoke, live app and runtime cleanup.
5. Merge latest main, release the next minor version, push main and verify deployment.

## Verification
- `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --configuration Release -m:1 --filter 'FullyQualifiedName~WorkflowEndNodeTests|FullyQualifiedName~WorkflowGraphTests|FullyQualifiedName~WorkflowRuntimeControlTests'`: 36 passed initially.
- Full backend suite: initial 1,204 passed / 1 failed (legacy terminal queued scheduling); scoped fix preserves old behavior. Subsequent full run: 1,207 passed / 1 failed (new wait-cleanup test ran against the earlier application build).
- Final current-code targeted suite, with filter `FullyQualifiedName~WorkflowEnd|FullyQualifiedName~WorkflowRuntimeControl|FullyQualifiedName~WorkflowGraph|FullyQualifiedName~WorkflowExecutionTests|FullyQualifiedName~WorkflowDecision`: **204 passed**, including all new End and PostgreSQL tests and both previous failures. Testcontainers uses the repository's pinned official GHCR Ryuk image.
- `npm run build`: passed; final production assets rebuilt after all editor changes. Existing bundle-size advisory remains.
- `helm lint deploy/helm/formicae` and `helm template formicae deploy/helm/formicae --namespace formicae`: passed.
- `PLAYWRIGHT_BROWSERS_PATH=/tmp/formicae-layout-browsers ./scripts/formicae-dev.sh prepare`: passed after the sandbox's local-socket restriction was resolved using approved escalation. First serial restore succeeded after parallel sandbox MSBuild failed without diagnostics. NuGet vulnerability lookup emitted NU1900 when network access was unavailable.
- `./scripts/formicae-dev.sh start`, `status`, `logs`, `stop`: passed; API healthy and UI responding. Final Playwright MCP inspection repeated against this managed runtime after the editor fix.
- Playwright MCP via the repository's configured `@playwright/mcp` package: End rendered with 1 input, 0 outputs and 0 model controls; console **0 errors / 0 warnings**; API requests succeeded. Screenshot and trace retained under `test-results/end-mcp/`. Paseo browser host was unavailable, so direct MCP stdio was used.
- Initial browser End tests: palette/save/reload passed; cancellation scenario used immediate fake workers, so it was corrected to verify End prevents another route's next task from launching. Actual worker cancellation is covered by runtime tests.
- `npm run test:smoke -- --workers=1`: 101 passed / 3 failed. End reload test now explicitly reopens its saved definition; two existing loop tests exposed a target-filter typo introduced by End and corrected before rerun. Targeted rerun (`npm run test:smoke -- --workers=1 --grep 'End palette|End completes|workflow editor round-trips loop settings|trigger and loop nodes can be created'`): **4 passed**, covering all three failures and the End runtime scenario. Final `npm run build` passed after the selector correction.
- `./scripts/run-k8s-e2e.sh`: **32 passed**, no failures; temporary cluster cleaned up.
- Shared-runner coordination: all existing suites finished; explicit release sent at 22:09 UTC, then hold further heavy local work for the font agent's verification window, extended by coordination through 22:32 UTC and explicitly released early at 22:28 UTC after all 26 font batches passed; no source delegation. Release reservation: 0.31.0 after origin/main 0.30.0.

Tests added: **17 backend cases and 2 browser cases**. Removed: **0**. Existing tests edited: **0**.

Release/deployment verification pending.
