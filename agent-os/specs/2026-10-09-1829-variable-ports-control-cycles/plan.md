# Implementation plan

1. Save spec documentation and obtain the baseline revision approval required by the AGENTS.md instructions supplied in this conversation.
2. Reproduce the reported multiple-output connection with its actual types/control path, repair native connection behavior or compatibility feedback, and expose parameter types on hover with accessible descriptions.
3. Accept unbounded control cycles and persist fresh visit/route identity, scheduling progress and producer provenance while retaining acyclic joins, explicit loops, entrypoint rules and cancellation.
4. Verify native dragging of multiple outputs, incompatible types, save/reload/undo, self/two-node cycles beyond a finite sample, restart, pause/cancel, retries, data preparation and existing grouping/layout using targeted/backend/browser/Kubernetes checks.
5. Merge latest main, select the next unique minor release, align release docs, push main and verify exact-image deployment plus health.

## Verification and release progress

- Approved scope implemented; merged latest `origin/main` 66ce16a (0.29.1 group-drag fix), preserving native parent handling and layout. Release versions align to 0.30.0.
- `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-build --no-restore --configuration Release`: 1,191 passed, zero failed/skipped. Targeted cycle/migration/persistence checks passed; the final 16 cycle cases include selected-entry completion and later-visit parallel retry.
- `npm run build`: passed TypeScript and Vite production build; existing bundle-size advisory remains. `helm lint deploy/helm/formicae`: passed.
- Live Playwright MCP inspection: three saved sources (A.body, B.body, A.author), `body: string` title and `body output: string` accessible label, B→A control cycle, preserved visual group, three data edges and zero console errors. Screenshot: ignored artifact `test-results/variable-mcp/ports-cycles-0.30.0.png`.
- Test changes: added 19 backend cases and 3 browser cases; edited 7 existing backend cases across 5 test declarations (cycle acceptance and nullable migration history); removed zero. Native browser tests cover pointer dragging, source preservation/order, two outputs from one producer, mismatches, undo/redo, save/reload, feedback consumers and real infinite-cycle controls.

- `PLAYWRIGHT_BROWSERS_PATH=/tmp/formicae-variable-browsers npm run test:smoke -- --workers=1`: all 102 cases passed in 7.9 minutes, including merged group/layout regressions.

- Managed harness `./scripts/formicae-dev.sh start`, `status`, `logs`, `stop`: API/UI healthy while running, clean logs, both services stopped. Explicit bind checks confirmed ports 5000/5173 free; coordination notification sent to the user.
- `helm template formicae deploy/helm/formicae --namespace formicae`: rendered aligned 0.30.0 chart/image tags. Latest-main fetch before release still reports 66ce16a.

- `./scripts/run-k8s-e2e.sh`: all 32 cases passed in 14 minutes 51 seconds; the disposable cluster was cleaned up.

Release completion requires successful automated deployment from the final main commit, the exact `docker.io/limeray/hhnl-formicae-api:0.30.0` image and Healthy `/healthz`.
