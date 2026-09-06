# Applicable standards

## database/ef-migrations

- Never create or edit Entity Framework migration files manually.
- Always generate migrations with `dotnet ef migrations add <MigrationName>` from the repository workspace.
- If migration generation fails, fix the model, project configuration, or tooling issue and rerun `dotnet ef migrations add`; do not work around it by hand-writing migration or snapshot changes.
- Review generated migration and snapshot files before committing to confirm they match the intended model change.

Source: `agent-os/standards/database/ef-migrations.md`.

## Repository requirements

- Keep built-in and legacy execution behavior compatible; preserve user edits.
- Serialize .NET build/test processes that share outputs.
- Run targeted/full backend checks, frontend smoke/build, managed development harness and Kubernetes E2E for worker/runtime/migration changes.
- Report exact test outcomes and added/edited/removed counts.
- Append enum values without changing existing ordinals; align version files once per release.
- Application changes do not authorize unrelated live infrastructure, credential or global configuration mutation.
