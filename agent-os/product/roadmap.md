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
- Add user authentication.
- Add a permission system.
- Configure AI model/API settings through the UI.
- Support customizable workflows.
- Add a workflow editor.
- Support loops and triggers as configurable workflow nodes (available).
- Support parallel planning branches with an explicit join (available in 0.12.0); parallel shared-branch writes remain deferred.
- Support deterministic workflow decisions with durable route history (available in 0.13.0).
- Add workflow scripts.
- Support customizable personas with immutable per-version task context (available in 0.14.0).
- Support reusable custom agent tasks with typed inputs and persisted outputs (available in 0.15.0).
- Support per-workflow-step capabilities, environments, and secrets.
- Support reusable environment profiles with immutable workflow-default selection and a runtime timeout cap (available in 0.16.0).
  - MCP server integration.
  - Custom Docker base image.
  - Tool installs.
