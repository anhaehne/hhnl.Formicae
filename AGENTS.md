# Repository Agent Instructions

## Communication

- Always respond in English.
- For implementation work, read applicable standards directly into context. Do not ask the user how to format or inject standards.
- Keep changes scoped to the user's current request.
- Do not revert user changes unless explicitly asked.
- Always include how many tests were added/removed/edited in pull request summaries.

## Merging Policy

- After completing approved changes, merge the latest `origin/main`, increase the release version, push the result to `main`, and verify the automated deployment unless the user explicitly asks to keep the work local or changes the release scope. Do not leave merge/deployment as a suggested next step.
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

## Feature Baseline

- `agent-os/product/features.md` is the authoritative application feature baseline; read it before planning or implementing changes.
- Before implementing any feature or change (including fixes, refactoring, configuration, infrastructure or removals), update its requirements and interactions in the baseline. The user’s request authorizes work within that scope; no separate baseline approval is required.
- Record the revision date, implementation status and user request reference in the baseline. Retain historical approval records.
- Implement only the requested scope. Document scope changes in the baseline before implementation continues.
- Keep specs, roadmap and supporting documentation aligned with the documented baseline; the baseline takes precedence over conflicting scope.

## Product Context

Use these files to understand the product direction:

- `agent-os/product/features.md` — authoritative feature requirements, interactions and revision history.
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

- Each feature, fix, or documentation-only release merged into `main` must increase the current release version using Semantic Versioning. A bump inherited from an earlier feature or release does not count for the new release. Check the version on the latest `origin/main` before choosing the new version, and complete the bump before pushing the merged release.
- Increase the version once per feature release. Do not repeatedly bump it during development or verification unless the severity changes, for example from a patch-level bug fix to a minor feature.
- Keep `Directory.Build.props`, `deploy/helm/formicae/Chart.yaml`, `deploy/helm/formicae/values.yaml`, and release/deployment docs aligned to the same version.
- Use a patch bump for bug fixes and documentation-only release changes, a minor bump for backward-compatible features, and a major bump for breaking changes.

## Self-testing Formicae changes

- For implementation and pull-request comment work, use `node scripts/test-selection.mjs --base origin/main --plan` to explain branch and working-tree validation, then `--run` to execute local validation. Full applicable suites may be satisfied by verified CI on the exact final merged SHA instead of duplicated locally; run targeted local regressions and fast verification first. Review the selected families against the task's interactions; add `--family NAME` for consumers the manifest cannot infer. See `docs/testing.md`.
- Fast repository verification means `git diff --check HEAD`, a Release build of the backend test project when backend tests are selected, and `npm run build` in ClientApp when browser tests are selected. Selector/policy changes additionally run `node --test scripts/tests/test-selection.test.mjs`. Generic prose-only changes need documentation/whitespace checks; prompts, executable fixtures/examples and operational instructions require their consuming suites. Unknown or ambiguous changes use full applicable validation.
- During iteration, explicit `--family NAME --run` or native test filters may run focused regressions. Before completion, use changed-file selection plus any additional affected families. The runner includes branch commits, tracked edits, additions, deletions, both sides of renames and untracked files; prints reasons and discovered tests; and rejects discovery failures or empty selected families. Explicit families alone are focused validation, not proof of complete impact coverage.
- Use `./scripts/formicae-dev.sh prepare` once, then `start`, `status`, `logs`, and `stop` to run and troubleshoot the API and Vite UI inside an agent worker.
- For runtime or UI behavior changes, reproduce the behavior against the running application and use the configured Playwright MCP browser to inspect the page, console, network activity, screenshots, and traces.
- Run `npm run test:smoke` from `src/hhnl.Formicae.Api/ClientApp` for the three tagged basic health/navigation/error checks. For UI changes, also run affected browser families with the selector (or explicit spec files); shared frontend/editor changes require full browser regression. `npm run test:browser` retains the entire deterministic browser suite. Full CI remains required on the final merged main SHA; a subset or earlier branch result does not replace it.
- Run `./scripts/run-k8s-e2e.sh` for Dockerfile, Kubernetes manifest, job-runtime, migration/startup, or deployment-sensitive changes. Set `FORMICAE_E2E_KEEP_CLUSTER=true` only while actively troubleshooting and clean up the preserved cluster afterward.
- Report the exact verification commands and outcomes. In pull request summaries, include the number of tests added, removed, and edited.

## Implementation Guidance

- Treat GitHub and Azure DevOps as primary integration targets.
- Keep MVP work aligned with the static workflow: plan work item, implement work item, create pull request.
- Preserve future extensibility for customizable workflows, personas, tasks, and environments.
- For environment customization, account for MCP server integration, custom Docker base images, and tool installs.
- When planning/implementing a new feature, always check if there are framework native ways or existing packages to achieve the goal/part of the goal.
- When interfacing with an external component like an API, always check if there are framework native ways or existing packages before creating your own client.
