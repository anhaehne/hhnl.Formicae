# Standards

Relevant standard: `agent-os/standards/database/ef-migrations.md`. If durable visit storage requires model changes, generate migrations with EF tooling; do not hand-edit migration files or snapshots. Reuse existing durable activation and iteration patterns when suitable. No new dependency is required for port hints.
