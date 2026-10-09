# Product Roadmap

## Codex Skill Context

This skill is project-local to this repository. When this workflow requires user input, ask one concise question at a time and wait for the answer before proceeding.

## Phase 1: MVP

- Provide a Kubernetes-native orchestration layer.
- Integrate with Azure DevOps and GitHub for issue/work item management and source code operations.
- Create ephemeral agents that run in Kubernetes for specific tasks.
- Derive each agent's prompt and personality from the current task.
- Execute an initial static workflow:
  - Create a plan for a work item.
  - Implement the work item.
  - Create a pull request.
- Use an existing CLI as the agent harness.
- Require the CLI harness to support plan mode and goal mode.
- Require support for Claude Pro and Codex Pro subscriptions.
- Allow the CLI model/API endpoint to be selected and authenticated.

## Phase 2: Post-Launch

- Add a management UI.
- Add workflow observability.
  - Workflow Management uses a compact execution sidebar; definitions host Manual Start for their saved versions, and graph ports remain separate from execution status/timing (available in 0.27.0).
  - Visual investigation of running and historical executions, live and retained worker logs, immutable retry attempts, and evidence exports (available in 0.19.0).
  - Scheduling-boundary pause/resume and durable cancellation with runtime cleanup (available in 0.19.0).
  - Searchable execution history, saved filter views and shareable task investigation links (available in 0.19.0).
  - Automatic retry/backoff policies, execution retention policies, notifications, artifact storage, subworkflows and compensation remain future work.
- Add user authentication.
- Add a permission system.
- Configure AI model/API settings through the UI.
- Support customizable workflows.
- Add a workflow editor.
- Support loops and triggers as configurable workflow nodes (available).
- Represent workflow entrypoints as registered event nodes: built-in Start/Webhook, GitHub Issue created/Label added and Gitea Label added. Each integration owns its event settings and matching; the optional manual Start and selected-entry history remain available (0.22.0; approved revisions `workflow-start-nodes` and `integration-event-nodes`).
- Implemented in 0.24.0, approved baseline revision `github-issue-comment-waits`: a callable GitHub Issue commented node accepting a repository-scoped issue number, with durable event waits that resume the same execution and expose comment outputs.
- Support parallel planning branches with an explicit join (available in 0.12.0); parallel shared-branch writes remain deferred.
- Support ordinary task outputs with multiple connections and all-input dependency joins, including nested task branches and multiple terminal tasks (available in 0.21.0). Mixing these task graphs with loop, decision or explicit parallel control nodes remains future work.
- Support deterministic workflow decisions with durable route history (available in 0.13.0).
- Support deterministic sh/bash workflow scripts with live logs, scalar stdout bindings and retained exit codes (available in 0.20.0).
- Support customizable personas with immutable per-version task context (available in 0.14.0).
- Provide GitHub Issue created outputs (complete issue JSON string and numeric Issue id) and an Add issue comment task accepting typed issueId/text inputs (0.25.0; approved revisions `github-issue-created-output` and `github-issue-comment-task`).
- Support named scalar custom-task outputs and explicit input bindings with frozen producer provenance (available in 0.18.0).
- Include the pinned output contract in Agent/custom-task prompts and correct missing or invalid final output through at most two turns in the same conversation within the original timeout (available in 0.24.0; approved revision `agent-task-output-correction`).
- Support inline Agent task nodes with personas and editable custom-task prefilling (available in 0.22.0).
- Support reusable custom agent tasks with typed inputs and persisted outputs (available in 0.15.0).
- Support per-step environment selection, inheritance and immutable profile history (available in 0.17.0).
- Support per-step provisioning capabilities and selected secret-key references (available in 0.20.0).
- Support reusable environment profiles with immutable workflow-default selection and a runtime timeout cap (available in 0.16.0).
  - Native Codex and OpenHands stdio/HTTP MCP integration with selected secret aliases (available in 0.20.0).
  - Custom compatible worker images, pull policies and operator image-pull secrets (available in 0.20.0).
  - Ordered tool installation with per-tool deadlines and visible bootstrap logs (available in 0.20.0).
- Managed Dockerfile images prepared in Manage → Images and selectable as exact builds in Agent tasks/environments (0.23.0; opt-in Kubernetes builds and bundled/external registry). Production enablement requires node trust and rootless build-pool validation. Automatic artifact garbage collection remains future work.
