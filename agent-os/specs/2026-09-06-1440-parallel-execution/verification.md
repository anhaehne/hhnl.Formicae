# Verification

- Plan reviewed and approved by the independent planning agent before implementation. Runtime and retry review added coverage for polling errors, cancellation, accepted-launch bookkeeping failure, completed-activation recovery, and terminal results surviving log failures.
- `$env:DOCKER_HOST='npipe://./pipe/podman-machine-default'; dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --configuration Release -v quiet`: **375 passed, 0 failed, 0 skipped**, including real PostgreSQL migration and API-startup tests.
- Backend cases: **82 added, 2 migration cases edited, 0 removed**. Initial full run was 372 passed/2 failed because its legacy JSON comparison included the newly added nullable column; the comparison now preserves every original-column assertion and separately verifies null attempt IDs and empty activation storage. The final run includes one additional recovery regression.
- `npm run build`: passed; existing large-chunk advisory remains.
- `npm run test:smoke -- --workers=1`: **26 passed**. After the final Join-edge layout correction, the three Parallel cases passed again. Browser cases: **3 added, 6 existing catalog selectors edited, 0 removed**.
- Desktop (1600px), narrow (800px), and incomplete-node validation screenshots inspected. Saved representative images under `visuals/`.
- Managed worker harness `formicae-dev.sh prepare/start/status/logs/stop`: passed; API and UI healthy and stopped cleanly. The unchanged lockfile reports five high npm audit findings; dependency upgrades are outside this issue.
- `helm lint deploy/helm/formicae`: passed, 1 chart, 0 failures.
- `git diff --check`: passed.

- `./scripts/run-k8s-e2e.ps1 -ContainerCli podman`: **5 passed, 0 failed, 0 skipped**; isolated local kind cluster cleaned up by the harness.

Release/deployment verification will be linked in the issue solution comment after the main pipeline completes.
