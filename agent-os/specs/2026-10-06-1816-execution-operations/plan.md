# Implementation plan

1. Save spec, standards, research/code references and acceptance checklist before feature edits.
2. Implement durable log metadata/commit-safe cursor, attempt archives, search/inspection/export APIs, authenticated native SSE and control intents. Generate and review EF migration.
3. Implement bounded reliable worker delivery, runtime log recovery, post-persistence cleanup acknowledgment, restart reconciliation, pause/resume and actual cancellation.
4. Implement visual run investigation, iteration/attempt inspector, readable live/historical logs, historical search/saved filters/deep links/failure navigation and safe controls.
5. Run targeted tests, full backend tests, frontend build, browser smoke, dev harness/live browser console/network/screenshots/traces and Kubernetes migration/runtime E2E. Audit acceptance checklist; update product/deployment documentation and aligned versions; report test counts and exact outcomes.

Backend agent owns application models/contracts/services/store/persistence/endpoints/migration. Runtime agent owns callback/reporter/runtime lifecycle/orchestrator gates. Frontend agent owns investigation UI/API types/styles/browser tests. Root owns integration, docs/version, acceptance audit and PR publication.
