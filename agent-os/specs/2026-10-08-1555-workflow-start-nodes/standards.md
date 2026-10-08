# Applicable standards

No database model change is required. If one becomes necessary, follow `agent-os/standards/database/ef-migrations.md`:

# Entity Framework Migrations

- Never create or edit Entity Framework migration files manually.
- Always generate migrations with `dotnet ef migrations add <MigrationName>` from the repository workspace.
- If migration generation fails, fix the model, project configuration, or tooling issue and rerun `dotnet ef migrations add`; do not work around it by hand-writing migration or snapshot changes.
- Review generated migration and snapshot files before committing to confirm they match the intended model change.

Repository instructions require baseline approval, compatibility with pinned versions, version alignment, targeted tests and runtime/browser reproduction.
