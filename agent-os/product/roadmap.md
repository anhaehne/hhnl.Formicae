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
- Support loops, parallel execution, decisions, scripts, and triggers.
- Support customizable personas.
- Support customizable tasks.
- Support customizable environments.
  - MCP server integration.
  - Custom Docker base image.
  - Tool installs.
