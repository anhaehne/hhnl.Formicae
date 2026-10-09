# References

- src/hhnl.Formicae.Application/Integrations/IssueWorkflowEventDefinition.cs and Workflows/WorkflowEventDefinitions.cs: provider-owned registration, metadata and current entry-event adaptation.
- src/hhnl.Formicae.Application/Workflows/WorkflowEventService.cs: start dispatch and existing-issue suppression; waits need independent correlation.
- src/hhnl.Formicae.Api/GitHubWebhookHandler.cs: signature validation and issues-only start routing; issue comments reach the handler but are not dispatched to callable waits.
- src/hhnl.Formicae.Application/Workflows/WorkflowOrchestrator.Graph.cs, WorkflowOrchestrator.Controls.cs and WorkflowNodeDefinitions.cs: scheduling, operator pause, cancellation and control/loop constraints.
- src/hhnl.Formicae.Infrastructure/GitHub/OctokitGitHubApi.cs and tests/hhnl.Formicae.Tests/{WorkflowEventNodeTests,WorkflowGraphTests,WorkflowExecutionPersistenceTests,WorkflowRuntimeControlTests}.cs: existing package adapter and verification patterns to inspect during implementation.

## External contracts verified

- [GitHub webhook payloads](https://docs.github.com/en/webhooks/webhook-events-and-payloads): issue_comment actions, comment identity/creation timestamp and pull-request discrimination. Existing Octokit issue reads are reused.
- [EF Core conditional updates](https://learn.microsoft.com/en-us/ef/core/saving/execute-insert-update-delete): conditional ExecuteUpdate and affected-row checks are used inside a transaction with the workflow row lock.
