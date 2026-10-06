# Execution operations (0.19.0)

## Scope

User authorized improving workflow handling, usability and features after repairing PR #67, with a large-model subagent researching must-have and nice-to-have capabilities. Explicit requirements are live and retrospective worker-task logs and visual node investigation of running workflows. Research compared Temporal, Argo, n8n and GitHub Actions against the repository; the original static MVP and existing workflow authoring, loops, decisions, parallel planning, immutable definitions/settings and typed data connections remain intact.

## Must-have acceptance checklist

- [x] Persist built-in and custom stdout/stderr/worker errors with workflow/task/attempt/worker/stream/timestamp/message identities.
- [x] Bounded retryable callback delivery detects HTTP failures, deduplicates stable IDs, marks overflow/truncation, rejects stale attempts and preserves authoritative completion.
- [x] Capture complete available runtime logs before worker cleanup, acknowledge cleanup only after durable evidence persistence, and reconcile failed cleanup after restart.
- [x] Authenticated native SSE streams persisted logs with commit-safe resumable cursors, heartbeats and reconnect support.
- [x] Bounded history paging with task/attempt/stream/severity/text filters, older-page loading, follow/pause-follow, connection state and filtered log downloads.
- [x] Read-only run graph uses pinned definition including control/data edges, actual decision routes, parallel states and loop iterations; distinguish not-executed from failed.
- [x] Node inspection includes iteration/attempt, timing, failure, worker identity, logs/events, inputs/outputs/provenance and effective settings without credentials.
- [x] Preserve graph selection/viewport and known data on refresh failures; abort stale selection requests.
- [x] Preserve immutable prior attempts before retry; expose old evidence and block retry until prior runtime cleanup completes.
- [x] Search/paginate historical executions by status, definition/repository/text/date; preserve selected execution as new runs arrive.
- [x] Durable idempotent cancellation prevents new launches, terminates active runtimes, preserves partial logs, reconciles uncertain launches and survives restart.
- [x] Scheduling-boundary pause prevents new steps while recording active tasks, including parallel work; resume continues durable state.

## Included nice-to-have checklist

- [x] Shareable workflow/node/attempt investigation links survive refresh/back navigation.
- [x] Running/Failed presets and locally saved filter views.
- [x] Failure-first navigation.
- [x] Actual execution counts and durations without misleading conditional/loop percentages.
- [x] Credential-free JSON evidence export with pinned definition, attempts, events, decisions and typed inputs/outputs/provenance.

## Decisions and boundaries

Use installed .NET 10/ASP.NET Core native SSE, EF Core/PostgreSQL, React Flow/ELK and KubernetesClient rather than a new broker or engine. Serialize log append allocation under a workflow row lock so cursors cannot miss late commits. Bound lines, queues, pages, exports and browser memory. Optional worker message/attempt IDs retain callback compatibility. Controls take the orchestration lock; cancel persists intent before runtime deletion; pause does not suspend processes. Runtime cleanup follows durable persistence. Append enum values for compatibility. Use a separate branch stacked on repaired PR #67 with one aligned 0.19.0 version bump.

Further engine extensions from research—automatic retry/backoff, arbitrary replay/reset of successful side effects, changed-definition re-execution, schedules/notifications, artifact stores, subworkflows, compensation and distributed quotas—require separate execution/provider policies. Existing roadmap issues cover capabilities/secrets, scripts and environment tools. This release covers every acceptance item above; those broader extensions remain explicit product backlog, not implied implemented features.

No user mockups were supplied. Reuse the visual editor vocabulary for a distinct read-only run investigator. User instruction to continue supplies implementation authorization; save the lightweight spec without another approval pause.

Verification commands, acceptance coverage and artifacts: [verification.md](verification.md).
