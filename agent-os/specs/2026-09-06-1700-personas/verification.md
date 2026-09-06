# Persona verification

Release candidate: 0.14.0. Local verification complete; deployment and issue closure remain pending.

## Review

- Catalog/domain/API and persistence independently reviewed. Revision checks plus atomic PostgreSQL compare-and-swap protect edits and deletion; the built-in default remains immutable.
- Runtime integration independently reviewed. Sequential and parallel tasks resolve their exact definition node, use pinned persona context, preserve model and durable attempt settings, and retain uncertain-launch handling.
- Review repairs: malformed null-node payloads return validation errors; failed catalog conflict reloads retain form state; save responses preserve edits made while a version save is in flight.

## Completed checks

- Focused backend persona tests: 48 passed, 0 failed, 0 skipped, including real PostgreSQL persistence/concurrency, API permissions, definition snapshots, runtime and prompt composition.
- Full backend: `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --configuration Release -v quiet` passed 564 tests, 0 failed/skipped. Initial run exposed the existing layout test comparing a raw submission to the now-enriched saved document. Updated that test to assert the default snapshot and compare the same saved document with/without editor metadata; the full rerun passed. Test changes: 48 added, 1 existing case edited, 0 removed.
- Focused browser persona scenarios: 5 passed. Covers catalog conflicts/deletion, inheritance/overrides, saved/current/deleted context, read-only users, and edits/navigation during an in-flight version save.
- Final frontend: `npm run build` passed; `npm run test:smoke -- --workers=1` passed all 36 browser tests in 1.4m after review repairs. Browser tests added: 5, existing cases edited: 0, removed: 0. Production assets rebuilt. Desktop (1600px), narrow (800px), revision previews and both dialogs inspected; narrow screenshots wait for the navigation transition. Saved-revision previews read the authoritative baseline rather than undo-history metadata.
- Managed lifecycle: `python -X utf8 test-results/package-dev-validation.py`, then the existing worker container ran `scripts/formicae-dev.sh prepare`, `start`, `status`, `logs`, and `stop`. Build passed with zero warnings/errors; API healthy, Vite responding, both stopped successfully.
- Helm lint: 1 chart passed, 0 failures. Only the existing optional icon recommendation.
- Kubernetes: `./scripts/run-k8s-e2e.ps1 -ContainerCli podman` passed 5 tests, 0 failed/skipped, in 3m29s. The isolated cluster was removed; `podman ps` showed no remaining containers.

Independent review approved the save-generation guard, disabled workflow-switcher items during saves, explicit-reload edit protection, workflow-default revision notice and baseline-backed Undo previews. Deployment verification will be reported in the issue solution comment.
