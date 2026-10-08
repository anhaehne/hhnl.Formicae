# Standards for managed agent images

## database/ef-migrations

- Never create or edit Entity Framework migration files manually.
- Always generate migrations with `dotnet ef migrations add <MigrationName>` from the repository workspace.
- If migration generation fails, fix the model, project configuration, or tooling issue and rerun `dotnet ef migrations add`; do not work around it by hand-writing migration or snapshot changes.
- Review generated migration and snapshot files before committing to confirm they match the intended model change.

## Repository requirements

Follow AGENTS.md: GitHub/Azure DevOps integrations, framework/package checks, scoped edits, targeted/fast validation, browser inspection and Kubernetes E2E for implementation, one aligned SemVer bump per release branch, exact outcomes and test counts in PR summaries.

Planning-only docs require no migrations or runtime checks. No new standard/index changes.
