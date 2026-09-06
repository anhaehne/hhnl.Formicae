# Environment issue family implementation plan

Planning snapshot: 2026-09-06. No implementation is included in this document change. The user delegated planning decisions and independent review; this plan must receive another agent's review before implementation.

## Dependency order

**#19 reusable environments → #17 per-step selection / #21 custom images → #22 tool installs → #18 scoped secrets → #20 MCP → #16 capabilities.**

Preserve existing execution defaults when no environment is selected. Deliver cohesive, verified increments; close each issue only after its acceptance criteria are met and deployment is verified. The session authorizes committing to main, pushing and waiting for deployment; broader infrastructure or credential changes are not implicitly authorized.

## 1. Save planning context

Save this plan and the inspected source/research references before implementation. Read applicable product documents and `agent-os/standards/database/ef-migrations.md`; generate migrations using EF tooling when the persistent model changes.

## 2. Reusable environments (#19)

- Add management-only environment CRUD, stable IDs, revisions, enabled state and typed configuration persisted as JSON.
- Provide a built-in Default resolving current worker image, task-dependent browser/DinD requirements and deadline policy. Do not silently change existing runs.
- Model extensible image, bootstrap, MCP and runtime settings without accepting arbitrary container specs or raw CLI configuration.
- Validate configuration and references before use. Define deletion/disable behavior that preserves historical snapshots.
- Test CRUD permissions, invalid configurations, default compatibility and disabled/missing references.

## 3. Per-step selection (#17) and custom images (#21)

- Add optional task-node environment references and preserve them through legacy/v1alpha3 normalization.
- Resolve the selected environment before job creation. Persist ID, revision and non-secret effective configuration with each task run; retries use the recorded configuration.
- Extend the existing `AgentTask` → `OpenHandsAgentRunner` → `RuntimeJobSpec` path. Both Kubernetes and local-container runtimes must honor the same supported fields.
- Expose image reference and pull policy in environment details. Validate syntax before launch; expose pull/startup failures clearly.
- Custom images must retain the Formicae worker entrypoint/runtime contract. Document extending the published worker image. Private registry support references operator-managed credentials rather than creating global credentials.
- Test different environments across two steps, history stability after environment edits, invalid references, custom-image execution and missing worker entrypoint failures.

## 4. Tool bootstrap (#22)

- Run ordered configured commands once after checkout and before task execution, within the existing job deadline.
- Capture structured bootstrap status, exit code, redacted output and failure reason. Installation failure prevents agent execution.
- Avoid interpolating task input into shell command text. Document that bootstrap commands are trusted management configuration and have the selected environment's execution authority.
- Test installing and invoking a temporary executable, non-zero exit, timeout/cancellation and output visibility.

## 5. Scoped secrets (#18)

- Add a step-level allowlist of secret references, resolved immediately before launch. Inject only selected application secrets in addition to explicitly identified platform authentication needed to execute the task.
- Reuse per-job secret files/environment. Reject reserved names that overwrite Formicae controls or auth configuration.
- Do not persist secret values in workflow documents, task snapshots, context ConfigMaps, DTOs, command logs or exceptions.
- Centralize multiple-secret redaction across bootstrap, agent output, callbacks and runtime result/log paths. Handle multiline values and escaped representations where emitted; do not claim arbitrary transformed-secret detection.
- Replace container command-line secret values with supported env-file/secret handling and restricted temporary files. Verify cleanup.
- Test selected-only injection, missing references before launch, reserved names, redaction in all operator-facing paths and both runtime adapters.

## 6. MCP integration (#20)

- Model typed stdio and streamable HTTP server settings. Use scoped secret references from #18 for credentials.
- Compose per-job TOML with a serializer: stdio `command`/`args`/`env_vars`, HTTP `url`/`bearer_token_env_var`/`env_http_headers`, and supported tool allowlists.
- Preserve Playwright as part of existing default execution. Avoid copying arbitrary auth-mount configuration into restricted environments; prevent project-level configuration from widening restrictions.
- Verify the schema against the worker's actual installed Codex CLI version. Reject unsupported agent-provider combinations instead of silently ignoring settings.
- Test fake stdio and HTTP MCP servers, scoped credentials, malformed settings, secret-free config/logs and isolation between jobs.

## 7. Per-step capabilities (#16)

- Define a finite capability vocabulary with precise enforcement semantics: supplied integration credentials, configured MCP servers/tools, browser provisioning and nested-container access.
- Record the effective configuration in task history. Restrict exposed resources and credentials through runtime construction and supported CLI configuration, including resumed execution.
- Existing `--dangerously-bypass-approvals-and-sandbox` means MCP allowlists or prompt instructions cannot enforce arbitrary shell/filesystem/network restrictions. Do not claim those guarantees.
- Implement broader restrictions only after actual CLI/runtime enforcement is proven. Reject unsupported capability restrictions clearly rather than silently weakening them.
- Test absence of denied credentials, MCP tools/servers and Docker socket; use actual execution for CLI policy checks. Verify retry/resume retains the same policy.

## Delivery validation

- Targeted application, persistence, worker and runtime tests for each phase, followed by full fast repository verification.
- Frontend browser coverage for environment CRUD, step selection, permissions, validation and history display.
- Run the managed development harness and smoke suite; inspect UI screenshots.
- Run Kubernetes E2E for image/runtime/bootstrap/secret/startup changes. Verify generated migration compatibility when persistence changes.
- Obtain independent review for secret handling and capability enforcement. Repair findings and rerun relevant tests.
- Align semantic version files once per release. Record exact commands, counts and outcomes. Commit/push, monitor CI and deployment, then close completed issues with a concrete solution/validation comment.
