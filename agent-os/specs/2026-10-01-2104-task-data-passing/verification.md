# Verification: task data passing 0.18.0

## Changes

- Application: typed output schemas, explicit bindings, control-order and loop validation, strict completion parsing, durable structured outputs, frozen input/provenance preparation, retry cleanup and history mapping.
- Infrastructure: EF catalog persistence, generated two-column migration and authoritative Codex/OpenHands CLI response extraction. In-memory stores and EF workflow updates carry the added model fields automatically.
- UI: output editing, eligible producer selection, named handles, separate data edges, binding serialization, undo/redo, deletion, replacement and structured history.
- Documentation: approved lightweight spec, two-task example, roadmap and aligned 0.18.0 project/chart/image/deployment version.

## Commands and results

Run from the repository root unless noted:

- `/root/.dotnet/tools/dotnet-ef migrations add AddTaskDataPassing --project src/hhnl.Formicae.Infrastructure --startup-project src/hhnl.Formicae.Api --output-dir Persistence/Migrations` — generated successfully; reviewed the migration and snapshot (nullable task-run structured output JSON and empty-default catalog output JSON). No migration files were edited manually.
- `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --configuration Release --filter 'FullyQualifiedName~CustomTask|FullyQualifiedName~TaskDataPassing|FullyQualifiedName~WorkflowEditor'` — 150 passed, 0 failed, 0 skipped.
- `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --configuration Release` — 834 passed, 0 failed, 0 skipped.
- From `src/hhnl.Formicae.Api/ClientApp`, `npm run build` — passed. Vite reports its existing large-chunk warning.
- From ClientApp, `npm run test:smoke` — most recent concurrent run: 54 passed, 3 timed out (two persona tests and the expanded connection scenario exceeded its former 30-second total budget). The long new scenario now has a 60-second budget. The concurrent run is not reported as passing.
- From ClientApp, `npm run test:smoke -- --workers=1` — 57 passed, 0 failed, after the final native replacement fix and scenario timeout adjustment.
- `./scripts/formicae-dev.sh prepare` — successful (run once); `./scripts/formicae-dev.sh start` — successful; `./scripts/formicae-dev.sh status` — API healthy and UI responding; `./scripts/formicae-dev.sh logs` — inspected; `./scripts/formicae-dev.sh stop` — both processes stopped. Final status confirms stopped.
- `./scripts/run-k8s-e2e.sh` — 6 passed, 0 failed, 0 skipped. Initial run failed during Docker restore because bridge-network NuGet access timed out. `docker build --network host -f src/hhnl.Formicae.Api/Dockerfile -t localhost/hhnl-formicae-api:e2e .` succeeded and populated the normal build cache; rerunning the standard script passed. `kind get clusters` confirms no remaining clusters. No cluster was preserved.
- `git diff --check` — passed.

## Live browser reproduction

Used the configured Playwright MCP browser against the API and Vite UI started by the dev script, with fake adapters and in-memory persistence. Created producer and consumer catalog tasks and saved a workflow through the running API. Both tasks succeeded: producer stored `{"summary":"ready"}`; consumer prepared `Use ready` with the matching producer run/attempt identity. Inspected the distinct control/data edges and input source in the editor, then expanded structured outputs, resolved inputs, provenance and prompt in execution history. During the reproduction there were no console errors or warnings and inspected API requests succeeded. Screenshots and the trace are under `test-results/agent-browser/` (local verification artifacts).

The configured MCP browser initially lacked its expected binary. Installed the repository-pinned browser with `PLAYWRIGHT_BROWSERS_PATH=/root/.cache/ms-playwright npx @playwright/mcp install-browser chrome-for-testing` from ClientApp, then completed inspection and closed the browser.

## Test changes

20 test declarations added (18 .NET declarations exercising 35 new cases, plus 2 browser tests); 3 existing declarations edited (late callback outputs, old-schema insertion and legacy history comparison); 0 removed. Existing custom-task tests continue to cover free-text compatibility, failed execution, launch uncertainty and prepared-context integrity.
