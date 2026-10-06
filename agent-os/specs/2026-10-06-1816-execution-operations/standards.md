# Applicable standards

## Database migrations

Source: `agent-os/standards/database/ef-migrations.md`.

- Never create or edit Entity Framework migration files manually.
- Always generate migrations with `dotnet ef migrations add <MigrationName>` from the repository workspace.
- If generation fails, fix model/configuration/tooling and rerun; do not hand-write migration or snapshot changes.
- Review generated migration and snapshot files for the intended model change.

The standards index contains no other area standards. Apply repository guidance on existing/native packages, authorization, scoped changes, semantic version alignment, meaningful tests, runtime/browser validation, Kubernetes E2E and reporting exact commands plus tests added/removed/edited.
