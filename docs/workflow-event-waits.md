# Waiting for GitHub issue comments

Add **GitHub: Issue commented** between ordinary workflow nodes. The node accepts a positive issue number, either literal or a typed numeric output binding, and a connected GitHub repository. An empty repository selection uses the execution's repository. The repository and issue input are frozen when that activation enters its wait.

The node launches no worker. It remains **Waiting** until a signed `issue_comment/created` delivery matches its repository and issue. Pull-request comments, edits, deletions and earlier comments do not satisfy the wait. Bot comments qualify. Outputs are `commentId`, `body`, `author`, `url` and `createdAt`, all strings, available through the existing typed data bindings. Downstream scalar input limits still apply to large comment bodies.

Each activation accepts one comment and continues the same workflow instance and pinned definition version. Concurrent comments cannot fork the instance or enqueue additional continuations. Extra comments are not carried into a later activation: only after the instance enters a wait node again can a new comment trigger it. Independent branches explicitly defined in the graph may continue while a wait blocks its successors and joins. Sequential flows, ordinary task graphs, outer decision paths and supported loop bodies can contain waits; explicit parallel groups retain their existing Plan-only branch restriction.

The API persists comment deliveries in a durable inbox and wakes the scheduler; it never executes a continuation from a webhook request. The scheduler uses the existing distributed orchestration lock, while PostgreSQL row locking and a conditional wait claim retain one accepted event. Delivery and comment identity constraints suppress redeliveries. Accepted evidence is committed before task completion, so a restart between these writes recovers the same result. A comment identity watermark and the activation timestamp exclude old events and handle GitHub's second-resolution creation timestamps.

Operator pause retains matching evidence while blocking scheduling. Resume without a qualifying comment leaves the node waiting. Cancellation invalidates waits and prevents later events from reviving the execution. Execution investigation shows the target issue, attempt, activation boundary, input provenance and accepted event identity alongside scalar outputs.

## Configuration example

```json
{
  "id": "wait-for-review",
  "uses": "github.issue-commented",
  "wait": { "issueNumber": 42 },
  "nextStepId": "respond"
}
```

A binding replaces `issueNumber` with `issueNumberBinding: { "stepId": "choose-issue", "outputName": "issueNumber" }`. Its source must be a guaranteed preceding numeric output. `repositoryId` optionally selects a connected GitHub repository explicitly.

Configure `GitHubWebhooks:Secret` and subscribe the GitHub App to issue comments. Unsigned deliveries cannot populate wait evidence. Start, Webhook, Issue created and Label added remain entry events; Issue commented is a callable wait node with an ordinary input port.

## Deployment

Use matching API, worker and chart versions **0.29.1**, the current release including output correction, issue outputs, comments and waits. The API applies the generated `AddWorkflowEventWaits` migration at startup. It adds wait and inbox tables without rewriting saved definitions or execution history. Waiting uses no Kubernetes Job and requires no worker protocol change. Inbox retention, wait timeouts, author filtering and dedicated comment entry events are outside this feature's scope.
