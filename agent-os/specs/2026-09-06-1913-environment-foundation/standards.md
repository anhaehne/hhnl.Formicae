# Applicable standards

## database/ef-migrations

- Never create or edit Entity Framework migration files manually.
- Always generate migrations with `dotnet ef migrations add <MigrationName>` from the repository workspace.
- If migration generation fails, fix the model, project configuration, or tooling issue and rerun `dotnet ef migrations add`; do not work around it by hand-writing migration or snapshot changes.
- Review generated migration and snapshot files before committing to confirm they match the intended model change.

## Repository constraints

- Preserve built-in defaults and immutable workflow histories; do not rewrite old definition JSON.
- Reuse existing runtime, authorization, catalog and snapshot mechanisms.
- Serialize .NET build/test processes sharing outputs; record exact verification and test change counts.
- Use managed development harness and Kubernetes E2E for runtime/migration changes.
- No implicit infrastructure, credential, mount or global configuration mutation. Supported future capabilities must be implemented and tested rather than represented as unenforced claims.
