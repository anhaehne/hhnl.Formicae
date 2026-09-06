# Per-step environment verification

Release candidate: 0.17.0. Local verification completed; deployment checks remain pending.

- Targeted definition and orchestration tests: 62 passed. Full `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --configuration Release` passed 799 tests, 0 failed/skipped, in 15s. Final TRX: `test-results/backend/step-environment-final.trx`. Compared with 0.16.0: 30 added cases, 0 removed or existing cases edited.
- Coverage includes inheritance, explicit default opt-out, overrides, one catalog read per distinct ID, missing-reference cache, node errors, disabled drafts, immutable revisions, structural snapshot consistency, legacy compatibility, loops, parallel recovery and explicit retries. Initial fixture issues were corrected: JSON fixture construction, valid graph start and semantic comparison after JSON round trips.
- `dotnet ef migrations has-pending-model-changes --no-build` reports no changes since the last migration. No migration or database backfill is introduced.
- Managed lifecycle passed with current packaged sources inside the existing worker container: `scripts/formicae-dev.sh prepare`, `start`, `status`, `logs`, `stop`. Build: 0 warnings/errors; API healthy, Vite responding; both stopped.
- Helm lint: 1 chart passed, 0 failed, existing optional icon recommendation only.

- Final `npm run test:smoke -- --workers=1`: 55 passed in 1.9m. Six added cases, 0 removed or existing cases edited. Final production build passed and generated assets were refreshed.
- Independent backend/frontend review approved the implementation after a legacy inspector-preview repair: nodes saved before per-step snapshots now show their saved document environment when inheriting it. A historical-document browser regression covers this behavior after catalog deletion.
- Desktop 1600px, narrow 800px and per-task profile history screenshots were inspected and saved under `visuals/`.

- Kubernetes: `.\scripts\run-k8s-e2e.ps1 -ContainerCli podman` passed all 6 tests, 0 failed/skipped, in 4m24s. Isolated fixture cleanup completed. No Kubernetes cases added, removed or edited in this release.

Deployment evidence belongs in the issue solution comment after live verification.
