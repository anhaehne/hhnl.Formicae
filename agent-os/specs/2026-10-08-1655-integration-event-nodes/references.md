# Integration event nodes references

- `src/hhnl.Formicae.Application/Workflows/WorkflowStartDefinitions.cs`: current start-node validation/compilation and manual entrypoint rules.
- `src/hhnl.Formicae.Application/Workflows/WorkflowEventService.cs`, `src/hhnl.Formicae.Api/GitHubWebhookHandler.cs`, `src/hhnl.Formicae.Api/GiteaWebhookHandler.cs`: signed delivery dispatch, provider matching and delivery audit.
- `src/hhnl.Formicae.Api/WorkflowWebhookEndpoints.cs`: built-in authenticated webhook delivery contract.
- `src/hhnl.Formicae.Api/ClientApp/src/workflowEditor/catalog.ts`, `Inspector.tsx`, `src/workflowGraph.ts`: catalog, settings and legacy draft adaptation.
- `tests/hhnl.Formicae.Tests/WorkflowStartNodeTests.cs`, `WorkflowWebhookApiTests.cs`, `src/hhnl.Formicae.Api/ClientApp/tests/e2e/starts.spec.ts`: existing entrypoint and browser regression coverage.

Framework/provider documentation checked before implementation: [native .NET dependency injection](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/overview) and [GitHub webhook events and payloads](https://docs.github.com/en/webhooks/webhook-events-and-payloads#issues). Uses the existing signed webhook handlers, registered definitions and JSON contracts; no new package or database migration is needed.
