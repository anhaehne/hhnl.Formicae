# Implementation plan

1. Extend catalog, pinned snapshots, workflow settings, prepared provenance and task-run response/storage contracts.
2. Reuse scalar limits and validate bindings after snapshot resolution against the control graph.
3. Generate an EF migration with dotnet ef; capture strict output objects from authoritative Codex/OpenHands completion events.
4. Resolve persisted successful producers by definition step and iteration; freeze preparation across restart/retry and clear outputs on producer retry.
5. Add schema editing, source selection, named React Flow handles and distinct data edges with undo/redo and history.
6. Add API, catalog, definition, orchestration, persistence and browser coverage; run targeted/full tests, build, smoke, live browser inspection and Kubernetes E2E. Release as 0.18.0.
