# Repository Agent Instructions

## Communication

- Always respond in English.
- Keep changes scoped to the user's current request.
- Do not revert user changes unless explicitly asked.
- Always include how many tests were added/removed/edited in pull request summaries.

## Merging Policy

- When working directly on `main`, fetch from `origin` and check for new remote changes before starting edits.
- Update from the target base branch before merging branches.
- Review incoming changes for conflicts with current work and repository instructions.
- Do not overwrite or revert user changes unless explicitly asked.
- Prefer small, focused merges and resolve conflicts intentionally.
- Run relevant validation after resolving conflicts.
- Check version files when the merge includes source or documentation changes, following the existing `## Versioning` section.

## Agent OS Overview

This repository uses Agent OS project-local documentation and skills:

- Product context lives in `agent-os/product/`.
- Feature specs live in `agent-os/specs/`.
- Development standards live in `agent-os/standards/`.
- Agent OS skills live in `.agents/skills/`.

Read the relevant Agent OS files before planning or implementing non-trivial work.

## Product Context

Use these files to understand the product direction:

- `agent-os/product/mission.md` — product problem, users, and differentiator.
- `agent-os/product/roadmap.md` — MVP and post-launch scope.
- `agent-os/product/tech-stack.md` — chosen technologies and platform assumptions.

When work changes product direction, roadmap scope, or technology choices, update these files in the same change.

## Specs

Use `$agent-os-shape-spec` for significant features or ambiguous work that needs a plan before implementation.

Spec folders are created under:

```text
agent-os/specs/YYYY-MM-DD-HHMM-feature-slug/
```

Each spec should contain:

- `plan.md` — implementation plan.
- `shape.md` — scope, decisions, and context.
- `standards.md` — standards that apply.
- `references.md` — relevant existing code or external references.
- `visuals/` — screenshots, mockups, or diagrams if provided.

Do not create heavyweight specs for tiny mechanical edits.

## Standards

Standards are concise rules for future agents. Store them under:

```text
agent-os/standards/
```

Use subfolders by area, for example:

- `global/`
- `backend/`
- `frontend/`
- `database/`
- `testing/`
- `devops/`

Use `$agent-os-inject-standards` before implementation when relevant standards may apply.
Use `$agent-os-discover-standards` to extract repeated project patterns into standards.
Use `$agent-os-index-standards` after adding, renaming, or deleting standards files.

The standards index is:

```text
agent-os/standards/index.yml
```

Keep it alphabetized and ensure every standards `.md` file has a short one-line description.

## Agent OS Skills

Use the local Agent OS skills for their intended workflows:

- `$agent-os-plan-product` — create or update product docs.
- `$agent-os-shape-spec` — shape a feature spec and implementation plan.
- `$agent-os-inject-standards` — read relevant standards into context.
- `$agent-os-discover-standards` — document recurring repository patterns.
- `$agent-os-index-standards` — rebuild the standards index.

When invoking a skill, follow its `SKILL.md` instructions exactly.

## Current Product Defaults

- Frontend: React, TypeScript, Vite.
- Backend: .NET / ASP.NET Core.
- Database: PostgreSQL.
- Runtime target: Kubernetes.
- Agent execution: ephemeral Kubernetes workloads.
- DevOps integrations: GitHub and Azure DevOps.
- Agent harness: existing CLI with plan mode and goal mode.

## Versioning

- Increase the project version at most once per branch using Semantic Versioning.
- Do not increase the version again on the same branch unless the severity changes, for example from a patch-level bug fix to a minor feature.
- Keep `Directory.Build.props`, `deploy/helm/formicae/Chart.yaml`, `deploy/helm/formicae/values.yaml`, and release/deployment docs aligned to the same version.
- Use a patch bump for bug fixes and documentation-only release changes, a minor bump for backward-compatible features, and a major bump for breaking changes.

## Implementation Guidance

- Treat GitHub and Azure DevOps as primary integration targets.
- Keep MVP work aligned with the static workflow: plan work item, implement work item, create pull request.
- Preserve future extensibility for customizable workflows, personas, tasks, and environments.
- For environment customization, account for MCP server integration, custom Docker base images, and tool installs.
- When planning/implementing a new feature, always check if there are framework native ways or existing packages to achieve the goal/part of the goal.
- When interfacing with an external component like an API, always check if there are framework native ways or existing packages before creating your own client.
