# References

- https://github.com/anhaehne/hhnl.Formicae/issues/14
- `WorkflowDefinitionService.cs`: immutable version creation, draft validation and run resolution.
- `WorkflowModels.cs`, `WorkflowNodeDefinitions.cs`: optional document/task settings and legacy normalization.
- `WorkflowOrchestrator.cs`, `WorkflowOrchestrator.Parallel.cs`: common task preparation must preserve distinct launch-failure behavior.
- `ClientApp/src/App.tsx`, `WorkflowDefinitionsPage.tsx`, `workflowEditor/state.ts`: catalog routing, inheritance, save baseline and previews.
- Existing AI settings/integration service and EF store patterns for permissions, registration and optimistic updates.
