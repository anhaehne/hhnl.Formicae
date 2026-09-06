# Custom task verification

Release candidate: 0.15.0. Local verification completed; deployment verification remains pending.

## Independent review and repairs

- Catalog, definition snapshots, typed inputs, preparation, retries, history, runtime deadlines and callback correlation independently reviewed.
- Prompt rendering is byte-bounded before building output; malformed persisted numeric inputs fail validation rather than retrying indefinitely.
- Numeric input contract narrowed before release to values that round-trip exactly through decimal and browser JSON, within safe integer magnitude. UI preserves partial edits and rejects precision loss/underflow.
- Custom execution requires exact node identity, retains prepared inputs and attempts across uncertain launches, and persists immediate terminal results before optional audit writes.
- Custom callbacks write bounded logs only; polling owns final output and status. Both supported CLI paths enforce a worker deadline and process-tree cancellation without checkpoint side effects.
- Full-suite diagnosis found parallel API factories sharing the same named in-memory identity database and creating duplicate roles. Test-only per-factory database isolation plus a concurrent-host regression fixes this without production authentication changes.
- All-node browser coverage caught a Loop catalog mapping regression; the mapping was corrected and the complete browser suite rerun.

## Completed validation

- Catalog/API/PostgreSQL tests: 19 passed. Generated migration adds only the catalog table and nullable prepared execution JSON; upgrade regression preserves prior persona, decision, parallel and task-attempt data.
- Full backend: `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --configuration Release -v quiet --logger 'trx;LogFileName=custom-final.trx' --results-directory test-results/backend` passed 676 tests, 0 failed, 0 skipped. Includes 112 added cases and 2 edited migration cases; 0 removed.
- Browser: full `npm run test:smoke -- --workers=1` passed 43 tests in 1.6m, covering nine node types. After the final numeric precision UI refinement, the affected suite passed another 6 tests. Includes 7 added cases, 0 removed.
- `npm run build` passed; production assets rebuilt. Desktop (1600px) and narrow (800px) catalog layouts inspected, along with deletion, revision and history screenshots.
- Managed development lifecycle: packaged current sources and ran `scripts/formicae-dev.sh prepare`, `start`, `status`, `logs`, `stop` in the existing worker container. Build passed with zero warnings/errors; API healthy and Vite responding; both stopped successfully.
- Helm lint: 1 chart passed, 0 failures, existing optional icon recommendation only.
- Kubernetes: `.\scripts\run-k8s-e2e.ps1 -ContainerCli podman` passed all 5 tests, 0 failed/skipped, in 2m42s. Script exited 0; Podman had no remaining containers and kind reported no clusters after cleanup.

Deployment evidence will be posted in the issue solution comment after live verification.
