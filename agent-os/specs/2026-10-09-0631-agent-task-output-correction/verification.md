# Verification — 2026-10-09

Approved scope: baseline revision `agent-task-output-correction`, product-owner message “lgtm”. Initial verification used 0.24.0. Release integration merges main 0.25.0 and aligns the new feature release at 0.26.0, authorized by the product-owner message “Merge main, up the version if needed and push to main”.

## Automated checks

Commands run from the repository root unless otherwise specified.

| Command | Outcome |
| --- | --- |
| `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --verbosity minimal` | Final rebuilt suite: 1,101 passed, 0 failed. |
| `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --filter 'FullyQualifiedName~TaskOutputCorrection\|FullyQualifiedName~CustomTask\|FullyQualifiedName~AgentTask\|FullyQualifiedName~TaskDataPassing' --verbosity minimal` | 190 passed, 0 failed. Final full suite also covers the subsequent prompt-idempotence adjustment. |
| `npm run build` (ClientApp) | Passed; normal bundle-size warning. Updated tracked production assets. |
| `helm lint deploy/helm/formicae` | Passed; chart-icon recommendation only. |
| `git diff --check` | Passed. |

Tests: 39 cases added (35 backend, 4 Kubernetes), 0 removed, 1 existing case edited. Coverage includes both providers, authoritative completion extraction, malformed/intermediate/oversized events, invalid schema results, correction exhaustion, missing conversation identity, process failure, original deadline/cancellation, retained attempt evidence, trusted worker markers, and downstream output consumption.

## Running application and browser

`./scripts/formicae-dev.sh prepare` completed restore, build, and npm dependency installation, then browser installation failed because `/ms-playwright/__dirlock` is read-only. Installed the pinned browser separately from ClientApp using:

```sh
PLAYWRIGHT_BROWSERS_PATH=/tmp/formicae-output-browsers node ./node_modules/@playwright/test/cli.js install chromium
```

`./scripts/formicae-dev.sh start` succeeded on retry after an initial startup timeout under host load. `status` reported healthy API and responding UI; `logs api` captured application evidence. `stop` stopped the development services before smoke testing.

The configured browser MCP had no connected automation host, so local Playwright inspected the running UI. An inline Agent producer and reusable Custom consumer completed successfully: the producer prompt included the pinned output contract, its output was `{"summary":"ready"}`, and the consumer prompt was `Use ready`. Inspection found no console errors, HTTP errors, or unexpected failed requests; four React request cancellations were recorded separately. Screenshot, trace, and inspection data are under ignored `test-results/output-correction/`.

Initial smoke attempts encountered the missing pinned browser and then test timeouts under host load. The completed run used:

```sh
PLAYWRIGHT_BROWSERS_PATH=/tmp/formicae-output-browsers npm run test:smoke -- --workers=1 --timeout=120000
```

The run completed with 84 passed and one failure in the existing `adding Parallel keeps the canvas usable through validation` viewport assertion after Fit All. Its first failure trace was preserved at `test-results/output-correction/parallel-viewport-first-run/`. A single-case retry passed (1 passed, 0 failed):

```sh
PLAYWRIGHT_BROWSERS_PATH=/tmp/formicae-output-browsers npm run test:smoke -- --workers=1 --timeout=120000 --grep 'adding Parallel keeps the canvas usable through validation'
```

No layout code or browser assertions were changed to accommodate the failure.

## Worker runtime and Kubernetes

`./scripts/run-k8s-e2e.sh` failed during fixture setup: kind could not import the approximately 3.5 GB worker image within five minutes (1 passed, 13 setup failures). A second attempt with an isolated cluster name, `FORMICAE_E2E_CLUSTER_NAME=formicae-output-correction ./scripts/run-k8s-e2e.sh`, reproduced the import timeout (1 passed, 17 setup failures, including the four new cases). These are infrastructure setup failures; worker behavior assertions did not run. Both clusters were automatically removed; `kind get clusters` confirmed none remained. Kubernetes verification remains incomplete.

As additional runtime evidence, `node /tmp/formicae-output-container-check.cjs` ran the built worker image in four disposable containers with no network, using the same CLI probes as the Kubernetes tests. All four passed: OpenHands and Codex each corrected invalid output in one resumed turn (exit 0, canonical output), and each exhausted two correction turns with a clear failure (exit 1, no output). Results and CLI logs are under `test-results/output-correction/`; the helper is copied there as `native-container-check.cjs`. Image digest: `sha256:c76240de22aa31101af715e960b8463692e00179149bcf362da1893634a8355e`. These probes exercise the real worker executable and process path, but do not replace Kubernetes integration verification or contact models.

The actual installed native CLIs also accepted the resume argument forms in help mode (both exit 0); evidence is saved in `native-cli-arguments.json`. No model request was made for these checks.

## Main integration — release 0.26.0

Merged `origin/main` at `2e9af2d`, preserving GitHub issue-comment waits, issue event outputs, and Add issue comment. Incoming release 0.25.0 required a new minor version for this feature; application, Helm, worker-image defaults, deployment docs, and baseline now use 0.26.0. Generated frontend assets were rebuilt from the combined source.

Post-merge `npm run build` passed. `helm lint deploy/helm/formicae` and `helm template formicae deploy/helm/formicae --namespace formicae` passed runtime-setting checks, including 0.26.0 API/worker image references; templating with `--set agentJobs.dind.enabled=false` correctly disabled nested runtime. `git diff --cached --check` passed with no unresolved entries.

The parallel post-merge backend run (`dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --verbosity minimal`) had 1,130 passes and one failure: another collection temporarily replaced `CODEX_HOME` and wrote its auth file while an output-correction test read it. The isolated retry (`dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~TaskOutputCorrectionTests' --verbosity minimal`) passed all 30 cases. The new worker-output test collection now disables concurrent collections because native worker calls read process-wide configuration. Production code is unchanged by this test isolation.

Post-merge browser verification passed all 18 selected cases in five minutes:

```sh
# From src/hhnl.Formicae.Api/ClientApp
PLAYWRIGHT_BROWSERS_PATH=/tmp/formicae-output-browsers npm run test:smoke -- --workers=1 --timeout=120000 --grep 'Agent task|custom|producer|issue|wait'
```

Kubernetes verification remains blocked by the previously reproduced image-import timeout; the same failed infrastructure setup was not repeated during integration.

The rebuilt regression run covering both worker-output and Codex workspace collections passed 37/37:

```sh
dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --filter 'FullyQualifiedName~TaskOutputCorrectionTests|FullyQualifiedName~CodexWorkspaceTests' --verbosity minimal
```

Serial full-suite verification uses xUnit's [native RunSettings option](https://xunit.net/docs/config-runsettings):

```sh
dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-build --no-restore --verbosity minimal -- xUnit.ParallelizeTestCollections=false
```

The serial full suite passed 1,131/1,131 in 3 minutes 54 seconds. Test changes in this feature remain 39 added cases, 0 removed, 1 existing case edited; the collection marker adds no test cases. The rebuilt 37-case run verifies the final collection isolation.
