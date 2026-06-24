# Tech Stack

## Codex Skill Context

This skill is project-local to this repository. When this workflow requires user input, ask one concise question at a time and wait for the answer before proceeding.

## Frontend

- React
- TypeScript
- Vite

React with TypeScript and Vite is the preferred starting point for the management UI because it has strong ecosystem support, fast iteration, and enough flexibility for workflow editing, observability views, and administrative screens.

## Backend

- .NET
- ASP.NET Core for APIs and management endpoints
- Background services for workflow orchestration and platform integration

## Database

- PostgreSQL

PostgreSQL is the default persistence layer for workflow definitions, task state, audit data, configuration, and observability metadata.

## Other

- Kubernetes as the primary runtime and orchestration target
- Ephemeral Kubernetes workloads for agent execution
- GitHub integration for issues, pull requests, and repository access
- Azure DevOps integration for work items, repositories, and pull requests
- Existing CLI-based agent harness with plan mode and goal mode
- Configurable model/API endpoint authentication
- Support for Claude Pro and Codex Pro subscription-based usage
