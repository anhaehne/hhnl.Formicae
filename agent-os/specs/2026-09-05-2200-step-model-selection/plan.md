# Per-step AI configuration and CLI model selection

1. Save shaping documentation.
2. Add optional AI settings ID and model to agent definition steps; preserve graph round trips and pinned-version execution through loops and retries.
3. Resolve model as step override, workflow model, selected configuration default, then existing runner fallback. Route the selected credentials and reject unsupported ACP execution.
4. Discover Codex models using an ephemeral runtime job and the CLI app-server stdio model/list protocol, without an agent turn or checkout. Expose protected start/status endpoints and bounded sanitized results.
5. Add configuration/model selectors with refresh, loading, unsupported and error states. Preserve saved models absent from discovery. Record resolved launch settings in workflow events.
6. Validate backend, worker protocol, browser smoke and Kubernetes runtime behavior. Align a single minor version bump. Commit, publication and deployment are separate actions.
