# References

- `src/hhnl.Formicae.Api/ClientApp/src/WorkflowDefinitionsPage.tsx`: connect, validConnection, reconnect and native React Flow configuration.
- `src/hhnl.Formicae.Api/ClientApp/src/workflowEditor/Node.tsx` and `VariableSettings.tsx`: ports and ordered variable sources.
- `src/hhnl.Formicae.Api/ClientApp/src/workflowGraph.ts`: type/availability checks, serialization and native groups.
- `src/hhnl.Formicae.Application/Workflows/WorkflowDefinitionValidator.cs` and `WorkflowGraphDefinitions.cs`: cycle detection, explicit-loop compatibility and graph validation.
- `src/hhnl.Formicae.Application/Workflows/WorkflowOrchestrator.Graph.cs`, `WorkflowOrchestrator.cs` and `WorkflowVariableDefinitions.cs`: one-run scheduling, existing durable loop iterations, repeated-task evidence and frozen data preparation.
- `tests/hhnl.Formicae.Tests/WorkflowVariableTests.cs`, `WorkflowGraphTests.cs` and ClientApp `tests/e2e/variables.spec.ts`: regression coverage.
