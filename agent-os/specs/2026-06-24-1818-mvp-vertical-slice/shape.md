# Shape: GitHub-First Vertical Slice MVP

## User Need
The MVP needs a narrow, reliable path from a manual GitHub issue event to an automated execution slice so we can validate the product model without waiting on large platform integrations.

## Proposed Shape
An operator-triggered workflow where a GitHub issue is used as the input artifact. The trigger starts an API call into the app, which persists execution metadata in PostgreSQL and dispatches a headless OpenHands CLI run inside Kubernetes.

### Core Flow
- Operator posts or references a GitHub issue as the canonical task source.
- Manual API trigger starts processing.
- Backend validates trigger request and creates a run record.
- Runner executes workflow deterministically through OpenHands CLI with fake adapters.
- Results and state transitions are persisted and exposed back to the operator.

## Design Decisions
- **GitHub-first**: primary trigger and canonical request context are GitHub-centric for MVP scope.
- **Manual trigger only**: avoids webhook complexity and supports predictable demos.
- **Kubernetes-native**: use cluster-native resources/services already aligned with this repo’s runtime assumptions.
- **PostgreSQL persistence**: single source of truth for run status, logs metadata, and deterministic replay signals.
- **OpenHands headless execution**: command-line runner integrated as part of execution step.
- **Fakes over live integrations**: external providers are mocked to keep tests deterministic.

## Success Definition
- One deterministic run path from request to completion.
- Stateful visibility of statuses (`Queued`, `Running`, `Succeeded`, `Failed`).
- Repeatable tests with stable outputs regardless of external API availability.

## Risks / Assumptions
- External APIs are represented by fakes in MVP scope.
- Post-MVP work is required for auth hardening and event-driven triggers.
