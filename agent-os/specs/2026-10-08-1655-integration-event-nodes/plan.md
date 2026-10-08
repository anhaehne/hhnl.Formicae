# Integration event nodes implementation plan

1. Save spec documentation and revise the feature baseline; obtain and record approval of `integration-event-nodes` before implementation.
2. Introduce a shared event definition/registration contract with provider-owned configuration, validation and dispatch; register distinct built-in Start/Webhook, GitHub Issue created/Label added and Gitea Label added events. Preserve legacy serialized definitions and audit records using adapters.
3. Consume registered event metadata in the editor catalog and render event-specific settings. Replace workflow trigger terminology with event terminology, retaining compatibility boundaries. Update the built-in workflow template and upgrade existing editable workflows through new versions with an explicit Start event.
4. Route signed GitHub opened/labeled deliveries through the corresponding registered event handlers; retain webhook authentication, delivery audit, duplicate suppression, per-entry scheduling and selected-event history. Check official provider documentation and existing framework/package facilities before changing provider integration.
5. Add meaningful backend and Playwright E2E coverage for separate catalog entries/settings, manual execution, signed issue-created/label-added deliveries, webhook behavior and existing-definition compatibility. Run targeted tests, full repository .NET verification, frontend build and browser smoke; run Kubernetes E2E for startup/deployment-sensitive changes. Keep supporting documentation and version files aligned; retain the existing 0.22.0 branch bump unless severity changes.

## Implementation outcome

Implemented the approved revision in 0.22.0. Integrations implement `IWorkflowEventDefinition` and register stable keys, editor metadata, validation and delivery matching in `WorkflowEventRegistry`. The editor consumes `/api/workflow-events` and offers separate Start, Webhook, GitHub Issue created, GitHub Label added and Gitea Label added nodes. There is no universal provider/type selector. Registered integration matchers accept their own event/action names without changing a central dispatch switch.

New definitions persist the event type in `uses` and configuration in `event`. Compatibility adapters retain legacy serialized trigger fields and audit records. Startup creates an explicit manual Start event version for simple legacy built-in workflows; editing other existing definitions upgrades the draft and saving creates a new version. Historical versions remain unchanged. Signed GitHub opened/labeled deliveries and authenticated workflow webhook deliveries preserve scheduling, overrides, audit and duplicate suppression. Execution history identifies the selected event node.

Test changes relative to the original branch base: **53 cases added, 21 edited, 0 removed** (47 backend and 6 browser additions). The event redesign contributes 23 new cases beyond the earlier start-node revision. Coverage includes custom integration registration/dispatch, wrong-provider repository rejection, signed GitHub deliveries, built-in version upgrades and per-event editor settings/save/reload. The legacy loop smoke test now checks both the new event configuration and unchanged old trigger data.

## Verification

| Exact command | Outcome |
| --- | --- |
| `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --configuration Release --logger 'console;verbosity=minimal'` | 1,038 passed, 0 failed, 0 skipped. |
| `dotnet build src/hhnl.Formicae.Api/hhnl.Formicae.Api.csproj --no-restore --configuration Debug --verbosity minimal` | Passed; 0 warnings/errors. |
| `npm run build` from `src/hhnl.Formicae.Api/ClientApp` | Passed; checked-in assets refreshed. Existing bundle-size warnings remain. |
| `PLAYWRIGHT_BROWSERS_PATH=/workspace/hhnl.Formicae/test-results/browsers npm run test:smoke -- --config test-results/event-port-run/playwright.config.ts --workers=2` from `ClientApp` | 81 passed; one stale assertion expected `trigger.label` instead of saved `event.label`. |
| `PLAYWRIGHT_BROWSERS_PATH=/workspace/hhnl.Formicae/test-results/browsers npm run test:smoke -- --config test-results/event-port-run/playwright.config.ts --workers=2 --grep 'workflow editor round-trips loop settings'` from `ClientApp` | Corrected assertion passed, including old-version preservation. All 82 browser cases passed across the full run and targeted rerun. |
| `WorkflowWebhooks__Secrets__review=review-fixture-token /tmp/formicae-event-dev.sh start`, then `status`, `logs`, `stop`; `PLAYWRIGHT_BROWSERS_PATH=/workspace/hhnl.Formicae/test-results/browsers node /tmp/formicae-event-runtime-review.cjs` | Passed live registered-webhook review: unauthorized 401, authenticated 202, completed execution, replay 200, selected event marked Started and queued-event history. No browser console errors, HTTP errors or unexpected network failures. Client-canceled requests are recorded separately. Screenshot, results and trace saved in `test-results/event-runtime-review/`. Review services stopped. |
| `helm lint deploy/helm/formicae` | Passed; 1 chart, 0 failures. |
| `./scripts/run-k8s-e2e.sh > test-results/integration-event-k8s.log 2>&1` | 13 passed, 0 failed, 0 skipped; 10m 11s. API deployment, startup/migrations, persisted workflow execution, rollout diagnostics and real worker behavior passed. The suite removed its `formicae-e2e` cluster. |
| `git diff --check` | Passed. |

Browser/harness overrides are temporary ignored files and use randomly selected API port **34225** and UI port **45607**, as requested; tracked port defaults remain unchanged. The harness was prepared once earlier in the session. The configured browser MCP could not reach the worker loopback ports (`ERR_CONNECTION_REFUSED`); local Playwright performed the runtime page, console, network, screenshot and trace inspection. Its temporary MCP tab was closed.

The initial Kubernetes invocation failed before application startup because the interrupted session left a stopped `formicae-e2e-control-plane` container. After inspecting it, `kind delete cluster --name formicae-e2e --kubeconfig /tmp/formicae-e2e/kubeconfig` removed that disposable cluster and the complete rerun passed. The separately named `formicae-images-e2e` cluster belongs to work outside this suite and was left untouched.

Recovered the four root files after explicit user instruction, preserving the known approval-gate edits in AGENTS.md and baseline link in README.md. The complete .NET rerun passed after recovery. Project, chart, image and deployment documentation remain aligned at **0.22.0**, with no second branch version bump.

## Integration with completed Agent task work

The user authorized merging the latest main and pushing after the Agent task agent finished. Its completion notice identified `e55a52c1304f8e9cda4525028bfb41a0913f9b63`, with passing CI and deployment verification. The event-node work was saved as `d420313` before merging that main revision.

Resolved shared editor conflicts by retaining Agent task inline schemas, typed ports, persona settings and template prefilling together with the registered event catalog, event-specific inspector and event nodes without input ports. Retained both deployment documentation sections and rebuilt generated assets from the combined frontend source. Version files remain **0.22.0**. Conflict resolution itself adds/edits/removes **0 tests**; event-node feature counts remain **53 added, 21 edited, 0 removed** relative to incoming main.

| Exact verification command | Outcome for the merged code |
| --- | --- |
| `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --configuration Release --logger 'console;verbosity=minimal' > test-results/event-merge-dotnet.log 2>&1` | 1,044 passed, 0 failed, 0 skipped. |
| `dotnet build src/hhnl.Formicae.Api/hhnl.Formicae.Api.csproj --no-restore --configuration Debug --verbosity minimal > test-results/event-merge-debug-build.log 2>&1` | Passed; 0 warnings/errors. |
| `npm run build` from `ClientApp` | Passed; generated assets refreshed from the combined source. Existing bundle-size warnings remain. |
| `PLAYWRIGHT_BROWSERS_PATH=/workspace/hhnl.Formicae/test-results/browsers npm run test:smoke -- --config test-results/event-port-run/playwright.config.ts --workers=2 > /workspace/hhnl.Formicae/test-results/event-merge-browser.log 2>&1` from `ClientApp` | 81 passed; initial editor navigation timed out with `ERR_NETWORK_CHANGED` recorded in its trace, and an Implement-node Fit All viewport assertion failed. Agent task prefilling/persona and all integration-event cases passed. Failure traces were preserved in `test-results/event-merge-failures/`. |
| Targeted browser rerun from `ClientApp` (exact command below) | Both failed cases passed in isolation in 13.5s without source/test changes. All 83 browser cases passed across the full run and targeted rerun. Successful event screenshots retained in `test-results/event-merge-captures/`. |
| `helm lint deploy/helm/formicae` and `git diff --cached --check` | Passed. |
| `./scripts/run-k8s-e2e.sh > test-results/event-merge-k8s.log 2>&1` | 13 passed, 0 failed, 0 skipped; 12m 5s. The merged API deployment, startup/migrations, persisted workflow, rollout and worker checks passed. The suite cleaned up its temporary `formicae-e2e` cluster. |

Exact targeted browser rerun command from `ClientApp`:

```bash
PLAYWRIGHT_BROWSERS_PATH=/workspace/hhnl.Formicae/test-results/browsers npm run test:smoke -- --config test-results/event-port-run/playwright.config.ts --workers=1 --grep 'contextual insertion preserves downstream|adding Implement keeps' > /workspace/hhnl.Formicae/test-results/event-merge-browser-rerun.log 2>&1
```
