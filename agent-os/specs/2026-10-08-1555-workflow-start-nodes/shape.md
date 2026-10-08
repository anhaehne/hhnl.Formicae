# Workflow start nodes

Scope: approved baseline revision `workflow-start-nodes`, approved by the product owner in the conversation on 2026-10-08.

Start nodes are first-class entrypoints without input ports or agent workers. An optional single Manual node is the manual entrypoint; definitions without it are trigger-only. Existing issue-label triggers remain provider starts. Generic webhook starts use an operator-managed shared-secret reference. Each start connects to one execution entry, which can branch using the existing task graph model. Runs retain selected start identity in the durable queued event; external deliveries retain trigger audit records. Existing pinned definitions are read without rewriting; editor drafts gain a collision-safe manual start node.

Use existing System.Security.Cryptography, ASP.NET endpoint/configuration APIs and persisted workflow events rather than new packages or database columns. Joins wait for active predecessors; data bindings must be available from every entry that reaches the consumer. Retain current explicit control-node restrictions.

No visuals were supplied. Product alignment: customizable workflows, GitHub integration and Kubernetes orchestration. Azure DevOps integration remains deferred as in the baseline.
