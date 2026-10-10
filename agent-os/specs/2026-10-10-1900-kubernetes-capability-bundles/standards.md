# Standards for Kubernetes capability bundles

The repository standards index contains one applicable standard. Read directly following the repository communication preference. These rules apply to future ledger implementation; this documentation change introduces no migration.

## database/ef-migrations

Source: [Entity Framework Migrations](../../standards/database/ef-migrations.md).

- Never create or edit Entity Framework migration files manually.
- Always generate migrations with `dotnet ef migrations add <MigrationName>` from the repository workspace.
- If migration generation fails, fix the model, project configuration, or tooling issue and rerun `dotnet ef migrations add`; do not work around it by hand-writing migration or snapshot changes.
- Review generated migration and snapshot files before committing to confirm they match the intended model change.

## Repository requirements

The feature baseline is authoritative; update it before implementation and retain historical approval records. Reuse native framework/package capabilities. Preserve user changes, old immutable versions and selected secret references. Run targeted/fast validation and real browser/Kubernetes verification for behavior/runtime changes. Each released change aligns props/chart/values/deployment docs with a fresh semantic version; record tests added/removed/edited.
