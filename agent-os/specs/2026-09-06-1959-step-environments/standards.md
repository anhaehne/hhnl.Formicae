# Standards for per-step environments

The repository's indexed database standard applies when verifying that JSON-only metadata adds no schema changes. No new framework/package is required; reuse the existing resolver, picker, persistence endpoint and runtime bridge.

## database/ef-migrations

- Never create or edit Entity Framework migration files manually.
- Always generate migrations with `dotnet ef migrations add <MigrationName>` from the repository workspace.
- If migration generation fails, fix the model, project configuration, or tooling issue and rerun `dotnet ef migrations add`; do not work around it by hand-writing migration or snapshot changes.
- Review generated migration and snapshot files before committing to confirm they match the intended model change.

## Repository delivery requirements

- Preserve existing user changes and keep this issue scoped to per-step environment selection.
- Serialize .NET/API test builds and use the managed development harness for browser/runtime validation.
- Report exact verification results and added/removed/edited test counts.
- Align the minor release version across project, Helm and release documentation once.
- Keep live changes within explicitly authorized targets and the established release pipeline.
