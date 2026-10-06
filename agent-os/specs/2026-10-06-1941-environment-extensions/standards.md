# Applicable standards

Apply `agent-os/standards/database/ef-migrations.md`: never hand-create or edit migrations/snapshots; use dotnet ef migrations add, fix generation problems, and review generated files.

Follow AGENTS.md: preserve user changes, immutable snapshots and static MVP; use native packages/framework support for external APIs; align all release version files once; validate backend/browser/dev harness/runtime Kubernetes behavior; report exact commands and test additions/removals/edits. Update target branch before merge and resolve incoming changes intentionally.
