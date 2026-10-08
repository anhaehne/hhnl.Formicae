# Agent task plan

1. Save spec documentation.
2. Add an inline Agent task definition using the existing runtime and persona resolution.
3. Add editor settings and custom-task prefilling with editable copied values.
4. Verify backend, browser behavior, and release alignment.

## Verification

- `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --configuration Release`: 997 passed, zero failures.
- `npm --prefix src/hhnl.Formicae.Api/ClientApp run build`: passed.
- `helm lint deploy/helm/formicae`: passed.
- Local Playwright MCP: saved Agent task with selected persona and typed input/output ports inspected; zero console errors; API requests succeeded; screenshot retained under `test-results/agent-task-manual/`.
- `./scripts/run-k8s-e2e.sh`: blocked by the pre-existing stopped `formicae-e2e-control-plane` container (12 infrastructure failures, one test passed). No preserved cluster was requested or created by this change.
- Tests added: 6 declarations (7 expanded cases); removed: 0; edited: 0.
- `PLAYWRIGHT_BROWSERS_PATH="$PWD/test-results/browsers" npm --prefix src/hhnl.Formicae.Api/ClientApp run test:smoke -- --workers=1`: 76 passed and one existing mocked custom-history test failed a five-second text-visibility assertion. The new Agent task prefilling/persona/save/reopen/template-deletion test passed.

- Isolated history recheck: `PLAYWRIGHT_BROWSERS_PATH="$PWD/test-results/browsers" npm --prefix src/hhnl.Formicae.Api/ClientApp run test:smoke -- --config ../../../test-results/agent-task-history.config.ts --workers=1 --grep "custom history displays"`: passed (10.2 seconds). The temporary config reused the running test servers and was kept under ignored test-results; no source change was needed.
