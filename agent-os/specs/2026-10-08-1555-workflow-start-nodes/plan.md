# Workflow start nodes implementation plan

1. Save spec documentation and record baseline approval.
2. Add explicit manual, provider and webhook start nodes, compile to existing execution entries and validate entrypoints independently while retaining legacy behavior.
3. Update the editor, manual-start selection and execution investigation. Authenticate webhook deliveries using operator-managed ASP.NET configuration references and reuse trigger audit/deduplication.
4. Add targeted validation, execution, webhook and browser regression coverage; run the development harness, repository .NET tests, frontend build and deterministic browser smoke suite.
5. Align roadmap and release/deployment documentation and increase the version once to 0.22.0.

## Implementation and verification outcome

Implemented the approved revision and aligned the application and Helm version to 0.22.0. Test cases: 30 added (27 backend, 3 browser), 18 edited, 0 removed.

| Command | Outcome |
| --- | --- |
| `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --configuration Release --logger 'console;verbosity=minimal'` | 1,018 passed, 0 failed, 0 skipped. |
| `npm run build` from `src/hhnl.Formicae.Api/ClientApp` | Passed; generated assets refreshed. Existing bundle-size warnings remain. |
| `PLAYWRIGHT_BROWSERS_PATH=/tmp/formicae-playwright npm run test:smoke -- --workers=2` from ClientApp | 79 passed, 0 failed. |
| `./scripts/run-k8s-e2e.sh` | 13 passed, 0 failed; temporary cluster removed. |
| `helm lint deploy/helm/formicae` and `git diff --check` | Passed. |

Development harness: ran `./scripts/formicae-dev.sh prepare` once, followed by `start`, `status`, `logs`, and `stop`. Preparation restored and built successfully, but the default browser cache was not writable. `PLAYWRIGHT_BROWSERS_PATH=/tmp/formicae-playwright npx playwright install chromium` from ClientApp installed the pinned browser successfully. The configured Playwright MCP reported no connected automation host; direct Playwright covered page, console, network, screenshot and trace inspection instead.

The final live check used `WorkflowWebhooks__Secrets__review=review-fixture-token ./scripts/formicae-dev.sh start` and `PLAYWRIGHT_BROWSERS_PATH=/tmp/formicae-playwright node /tmp/formicae-start-runtime-review.cjs`. It verified HTTP 401 without credentials, HTTP 202 for an authenticated delivery, HTTP 200 for replay, completed execution, and the selected webhook node marked Started. Busy-orchestrator HTTP 503 responses were retried with the same delivery identifier. Browser console errors and failed browser requests: 0. Local evidence is in `test-results/dev/start-node-runtime.png` and `test-results/dev/start-node-runtime.trace.zip`; both are ignored runtime artifacts. Development services were stopped afterward.

An earlier browser run overlapped Kubernetes network creation and contained network-change failures and outdated graph-count assertions. The assertions were updated for explicit start nodes, and the complete isolated browser suite passed as reported above.
