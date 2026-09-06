# Verification

- Independent plan approval preceded implementation; persistence, evaluator, graph, retry and frontend integration received cross-agent review.
- `$env:DOCKER_HOST='npipe://./pipe/podman-machine-default'; dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --configuration Release -v quiet`: **516 passed, 0 failed, 0 skipped**, including PostgreSQL transactions, migration/reload and history permissions.
- Backend tests: **141 cases added, 0 existing cases edited, 0 removed** (121 evaluator/graph, 14 execution/persistence, 6 retry/history).
- `npm run build`: passed, with the existing large-chunk advisory.
- `npm run test:smoke -- --workers=1`: **31 passed**. Browser cases: **5 added, 0 existing cases edited, 0 removed**. Initial selector failures were corrected with explicit accessible select names and the existing expansion button's actual accessible label.
- Desktop, narrow, history and incomplete-node validation screenshots inspected; representative files saved under `visuals/`.
- Managed `formicae-dev.sh prepare/start/status/logs/stop` in the existing local worker image: passed; API/UI healthy and stopped cleanly. Existing lockfile audit findings remain outside this issue.
- `helm lint deploy/helm/formicae`: passed, 1 chart, 0 failures.
- `git diff --check`: passed after normalizing generated index.html line endings.

- `./scripts/run-k8s-e2e.ps1 -ContainerCli podman`: **5 passed, 0 failed, 0 skipped**; the isolated local cluster was cleaned up.

Main pipeline and live deployment evidence will be linked in the issue's solution comment.
