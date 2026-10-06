# Complete workflow and environment extensions (0.20.0)

## Scope and authorization

Implement all six remaining issues explicitly requested by the user: #12 scriptable steps, #16 per-step capabilities, #18 per-step secrets, #20 environment MCP, #21 custom execution images and #22 tool installs. The user authorized GitHub, pushes and deployment for the remainder of this session. Consult a large-model subagent for implementation ideas, then implement and verify every acceptance criterion end to end. Current deployed base is 0.19.0, main e97bf18.

The Agent OS shaping skill requires plan mode; the user has explicitly instructed implementation in the current mode. Save this lightweight execution spec before edits without another planning/approval stop. Apply database/ef-migrations standards; generate migrations with EF tooling.

## Decisions

Use typed extensions of existing schema1 environment snapshots. Images are worker-compatible execution images (customize FROM the Formicae worker image), with validated reference, pull policy and named pull secrets. Tool installs are named bounded shell bootstrap scripts, executed before task start with logs and explicit failures. MCP servers support native Codex and OpenHands stdio/HTTP configuration; credentials are supplied only through selected secret environment aliases.

Step capabilities restrict Formicae-managed browser, nested-container, named tool-install and named MCP resources. Null preserves defaults; an empty list grants none. This is a platform provisioning policy, not an OS shell/network sandbox. Validate all selections; never silently run unsupported harness integrations.

Step secrets contain references only: environmentName, secretName and key. Kubernetes preflights and injects only selected keys from Secrets in the configured job namespace, never envFrom for user references, never copying/deleting user Secrets. Container execution resolves explicitly configured local secret references. Reject reserved environment names and collisions; redact selected values from logs, final output, failures and evidence. Missing references fail before execution. No secret values in definition snapshots, API responses, UI or audit.

Scripts are first-class builtins.script tasks with sh/bash, body, bounded timeout and workspace/repository working-directory selection. They use the same ephemeral runtime, attempts, logs, controls, retries and environment snapshots; no AI credentials or automatic commit/push. Store actual output and exit code; nonzero exits fail deterministically. Append enum values for compatibility.

## Acceptance audit

| Issue | Required result | Verified |
| --- | --- | --- |
| #12 | Script editor/validation; isolated worker execution; output and actual exit code retained; deterministic failure/timeout/cancel/retry tests | Pending |
| #16 | Restricted capabilities configurable; launch/bootstrap/MCP/browser/container provisioning honors limits; effective selections visible in run audit | Pending |
| #18 | Only selected external secret keys injected; missing keys fail preflight; values masked in all operator evidence; reference-only UI/API | Pending |
| #20 | Typed environment MCP catalog/editor/snapshot; native selected-harness injection; scoped credentials; real adapter tests | Pending |
| #21 | Environment-specific worker image/pull settings; validated references before launch; visible pinned/runtime settings; default behavior preserved | Pending |
| #22 | Named bootstrap installs before task; bounded output and timeout; failure stops task; documented executable example | Pending |

Preserve existing disabled drafts, immutable/deleted snapshots, revision conflicts, typed custom-task data passing, editor undo/redo, viewers, default workflows and runtime cleanup ordering. One aligned minor version bump to 0.20.0 on this branch.

No mockups supplied. Extend existing environment catalog and workflow node inspector. Validation includes targeted/full backend, frontend production build/full smoke, real dev-harness Playwright MCP inspection, Docker builds, packaged CLI checks, and real Kubernetes secrets/scripts/image/bootstrap E2E. Deploy only after green checks.
