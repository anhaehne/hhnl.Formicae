# MVP Workflow

The MVP workflow is intentionally static so agents can understand and test it quickly.

## Trigger

`POST /api/workflows/github-issue` creates a workflow from:

- `issueUrl`
- `repositoryUrl`
- `baseBranch`
- `model`

The workflow starts in `Queued` with `CurrentStep = None`. The worker monitors the work item provider and advances phases only when the issue has the expected labels: `ready-to-plan` starts planning, and `ready-to-implement` starts implementation after a plan exists.

## State Machine

```mermaid
stateDiagram-v2
    [*] --> Queued
    Queued --> Planning: ready-to-plan label
    Planning --> Implementing: plan succeeded
    Planning --> Failed: plan failed
    Implementing --> CreatingPullRequest: ready-to-implement label and implementation succeeded
    Implementing --> Failed: implementation failed
    CreatingPullRequest --> Reviewing: draft PR created
    CreatingPullRequest --> Failed: PR creation failed
    Reviewing --> AddressingComments: PR comments found
    AddressingComments --> Completed: comments addressed
    AddressingComments --> Failed: comment-addressing failed
    Completed --> [*]
    Failed --> [*]
    Canceled --> [*]
```

## Task Runs

Each agent or integration step is stored as a task run:

- `Plan`: fetches GitHub issue context and asks OpenHands to produce an implementation plan.
- `Implement`: creates or reuses a branch and asks OpenHands to apply the plan.
- `CreatePullRequest`: opens a draft pull request for the branch.
- `AddressComments`: once the pull request has comments, asks the agent to address issue comments and review comments from the PR. On success, Formicae posts or updates a marked top-level PR summary comment.

Completed task runs are reused on retry. This makes workflow advancement idempotent at the step level. `AddressingComments` is shown as a diagram phase for readability; in persisted workflow state this is `Reviewing` with `CurrentStep = AddressComments`. After PR creation, the workflow remains in `Reviewing` until pull request comments exist. Comment monitoring reads both top-level PR issue comments and inline review comments, but ignores comments containing the hidden `<!-- formicae:... -->` marker so automation comments are not treated as user feedback even when the same account is used. When comments are found, the worker runs `AddressComments`; a successful run completes the workflow, and a failed run marks the workflow `Failed`.

## Local Iteration

Fake adapters are the default. They let tests and local API runs complete the whole workflow without GitHub credentials, Kubernetes, OpenHands, or PostgreSQL.

Use real adapters only after the local vertical slice is passing.
