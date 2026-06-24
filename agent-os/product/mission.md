# Product Mission

## Codex Skill Context

This skill is project-local to this repository. When this workflow requires user input, ask one concise question at a time and wait for the answer before proceeding.

## Problem

DevOps teams need a way to orchestrate AI agents that can work on individual tasks from platforms like GitHub and Azure DevOps without relying on developer workstations or one-off manual agent runs. Teams also need visibility into what agents are doing, how workflows progress, and how task-specific environments and agent behavior are configured.

## Target Users

The direct users are DevOps engineers who configure, operate, and monitor the platform. Software engineers and product teams interact with it indirectly through connected DevOps platforms such as GitHub issues, pull requests, Azure DevOps work items, and code repositories.

## Solution

The platform provides a mostly self-hosted orchestration layer for running background agents in Kubernetes. It creates ephemeral agents for specific tasks, supports task-aware prompts and personalities, lets users define custom execution environments, and can run agents in parallel without depending on a developer's local machine. Model access remains external, but orchestration, workflow execution, environment control, and operational insight stay inside the user's infrastructure.
