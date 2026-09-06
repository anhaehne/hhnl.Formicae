# References

- https://github.com/anhaehne/hhnl.Formicae/issues/11
- `WorkflowNodeDefinitions.cs`: current Loop/Trigger normalization and graph invariants.
- `WorkflowParallelDefinitions.cs`: structured branch ownership and joins.
- `WorkflowOrchestrator.cs`, `WorkflowOrchestrator.Parallel.cs`: dispatch, durable group recovery and cursor transitions.
- `WorkflowModels.cs`, `Interfaces.cs`, `EfWorkflowStore.cs`, `InMemoryWorkflowStore.cs`: contracts and transaction boundary.
- `ClientApp/src/workflowGraph.ts` and `workflowEditor/`: editor adapters, catalog, inspector and named ELK ports.
- `WorkflowParallelPersistenceTests.cs`, `WorkflowParallelOrchestratorTests.cs`, `ClientApp/tests/e2e/editor.spec.ts`: migration/restart and browser patterns.
