# Environment foundation verification

Release candidate: 0.16.0. Local verification completed; deployment checks are pending.

## Independent review and repairs

- Catalog, strict configuration schema, optimistic concurrency and generated migration reviewed. Unsupported image/tools/MCP configuration and unknown fields fail validation; quoted numeric values do not bypass integer contracts.
- Current-reference resolution, immutable saved snapshots, default compatibility and disabled drafts independently reviewed.
- A definition test caught normalization dropping the new document fields. Normalization now preserves both environment selection and snapshot.
- Runtime review confirmed shared cap resolution, propagation when prior policy was implicit, preserved no-cap behavior and profile-only audit on reattachment.
- Review identified helper-process cancellation during checkout/status operations. The worker now drains both output streams and terminates the process tree on cancellation; real-process regressions cover cancellation and large stderr output.
- Frontend review covered delayed-save draft retention, saved-baseline previews, undo, read-only permissions and conflict reload. Invalid empty environment references remain visible instead of appearing as the default.

## Validation

- Final full backend run: `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --configuration Release -v quiet --logger 'trx;LogFileName=environment-final.trx' --results-directory test-results/backend` passed 769 tests, 0 failed/skipped, in 15s. TRX confirms the direct runner guard regression is included. Compared with 0.15.0: 93 added cases, 0 removed, 0 existing cases edited.
- Frontend production build passed. Full `npm run test:smoke -- --workers=1` passed 49 tests in 1.8m, including invalid empty-ID preservation. Six added cases, 0 removed, 0 existing cases edited. After the final spacing refinement, the six affected browser tests passed again in 14.7s. Desktop 1600px, narrow 800px, deletion, settings revision and history screenshots were inspected and saved under `visuals/`.
- Managed development lifecycle passed using current packaged sources and `scripts/formicae-dev.sh prepare`, `start`, `status`, `logs`, `stop` inside the existing worker container. Build: 0 warnings/errors; API healthy, Vite responding, both stopped.
- Helm lint: 1 chart passed, 0 failed; existing optional icon recommendation only.
- Generated `20260906175629_AddExecutionEnvironments` adds only the environment catalog table. PostgreSQL migration/concurrency and API/catalog tests passed.
- Kubernetes: `.\scripts\run-k8s-e2e.ps1 -ContainerCli podman` passed all 6 tests, 0 failed/skipped, in 3m21s. One added case, 0 removed or existing cases edited. The new test observes a running process, verifies propagated worker timeout variables, native `DeadlineExceeded` without scheduler polling, and Job/Pod cleanup. Its initial post-termination log assertion was corrected to capture startup evidence before Kubernetes removed the Pod; no production change was needed. The complete suite then passed.

Deployment evidence will be posted in the issue solution comment after live verification.
