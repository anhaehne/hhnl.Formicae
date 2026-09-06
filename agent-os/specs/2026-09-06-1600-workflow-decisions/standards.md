# Applicable standards

- Repository AGENTS.md: scoped implementation, preserve user changes, independent plan review, version alignment, exact test counts, managed lifecycle and browser/runtime verification.
- `agent-os/standards/database/ef-migrations.md`: generate migrations and model snapshots through EF tooling; never handwrite migration classes.
- Existing React Flow primitives, explicit save/undo state, named handles and graph adapters remain the editor integration boundary.
- Serialize scoped EF operations and hold no database transaction over external work.
