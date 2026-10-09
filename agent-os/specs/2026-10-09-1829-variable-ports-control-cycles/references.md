# References

- `src/hhnl.Formicae.Api/ClientApp/src/WorkflowDefinitionsPage.tsx`: connect, validConnection, reconnect and native React Flow configuration.
- `src/hhnl.Formicae.Api/ClientApp/src/workflowEditor/Node.tsx` and `VariableSettings.tsx`: ports and ordered variable sources.
- `src/hhnl.Formicae.Api/ClientApp/src/workflowGraph.ts`: type/availability checks, serialization and native groups.
- `src/hhnl.Formicae.Application/Workflows/WorkflowDefinitionValidator.cs` and `WorkflowGraphDefinitions.cs`: cycle detection, explicit-loop compatibility and graph validation.
- `src/hhnl.Formicae.Application/Workflows/WorkflowOrchestrator.Graph.cs`, `WorkflowOrchestrator.cs` and `WorkflowVariableDefinitions.cs`: one-run scheduling, existing durable loop iterations, repeated-task evidence and frozen data preparation.
- `tests/hhnl.Formicae.Tests/WorkflowVariableTests.cs`, `WorkflowGraphTests.cs` and ClientApp `tests/e2e/variables.spec.ts`: regression coverage.

- [Testcontainers native configuration](https://dotnet.testcontainers.org/custom_configuration/): `TESTCONTAINERS_RYUK_CONTAINER_IMAGE` preserves the resource reaper while choosing a registry. Installed Testcontainers 4.14.0 embeds Ryuk 0.14.0 (Docker Hub index digest `7c1a8a9a47c780ed0f983770a662f80deb115d95cce3e2daa3d12115b8cd28f0`).
- [Official Ryuk publisher workflow](https://github.com/testcontainers/moby-ryuk/blob/main/.github/workflows/publish-docker-image.yml): publishes the same release to Docker Hub and GHCR. `docker manifest inspect --verbose ghcr.io/testcontainers/ryuk:0.14.0` verified Linux amd64 manifest digest `f0456560ea5b4acdbed0da0efc33b5f9dd6bc1e59f2337106826dcb5b0b0e981`.
