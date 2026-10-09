# GitHub issue-comment waits

Status: approved for implementation on 2026-10-08 by the product owner; approval reference: conversation message “Approved” covering baseline revision github-issue-comment-waits and its single-instance clarification.

## Scope

The user requested a GitHub commented-on-issue trigger accepting an issue ID, callable as a normal node, waiting for a comment and continuing the same flow. Introduce durable node suspension and event-driven resumption, with GitHub Issue commented as the first integration implementation.

## Proposed decisions

Issue ID means a positive issue number scoped to a connected GitHub repository, literal or bound through existing typed inputs. Default to the execution repository; allow explicit selection. The first new comment after activation qualifies, including bot comments; edits, deletions and pull-request comments do not. Expose scalar comment outputs. Waiting blocks dependent nodes while independent work continues. Dedicated comment-start behavior, author filters and timeouts are outside this proposed scope.

Reuse PostgreSQL/EF persistence, orchestration locking and integration registration. Before implementation, check framework and existing Octokit facilities for validation and webhook data; extend existing provider adapters rather than create another GitHub client or workflow engine.

## Single-instance resumption

User clarification: multiple comments must not cause multiple resumptions or parallel copies of one workflow instance. Each wait activation has one atomic winning comment and one continuation. Serialize advancement per workflow instance across replicas. Additional comments do not enqueue continuations or get saved as triggers for the next wait. Another comment can trigger only after the instance re-enters a wait node and arms a new activation; pre-activation comments and consumed comments remain ineligible. This preserves explicit workflow graph branches within the same execution.

## Context

No visuals supplied. Existing event registration and task scheduling are references. This extends customizable workflows and durable execution visibility within the current mission and tech stack. The authoritative scope and approval status are in features.md; all proposed decisions are reviewable as part of that revision.
