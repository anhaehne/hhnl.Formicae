# Task graph connections

Version 0.21.0 supports multiple ordinary task output connections. Connect an output to several task inputs to run those tasks concurrently. A task with several incoming connections waits until every predecessor succeeds. Nested forks, joins and multiple terminal tasks are supported; the execution completes only when every reachable task succeeds.

In v1alpha3 definition JSON, `nextStepId` retains the first successor and `nextStepIds` contains additional successors. The editor preserves these connections when saving and reloading. Each connection can be disconnected separately. Named data output connections remain separate from execution dependencies. A custom-task input may bind to any ancestor whose completion is guaranteed by the dependency graph.

Tasks persist their own execution attempts, outputs and runtime identity. Restarting orchestration reattaches to existing jobs. A failed task blocks dependent work; retrying keeps successful sibling results. Planning inputs are assembled deterministically from the latest completed planning ancestors, and the entry planning context is frozen.

Task graphs support all ordinary task types and single-entry trigger nodes. Legacy graphs reject joins that depend on a task unreachable from their execution entry. From 0.22.0, explicit [start nodes](workflow-start-nodes.md) can converge on shared work: each run waits for predecessors active under its selected entry, while bindings still require an available producer from every entry that reaches the consumer. From 0.30.0, ordinary control cycles and self-connections are supported without an iteration limit. Combining graphs that have multiple ordinary successors with loop, decision or explicit parallel control nodes remains unsupported. Existing explicit controls retain their behavior in workflows without multiple ordinary output connections.

## Visual groups

Ctrl/Cmd-click multiple nodes and choose **Group selected**. The group inspector edits the name shown on the canvas and its background color (gray, blue, green, yellow, orange, purple or pink). Drag the group header to move all members together; drag an individual member to change its position. Bounds follow the members, including after Arrange. From 0.30.0, group containers retain their measured dimensions during drag updates so their background and title remain visible throughout movement.

The Members checkboxes add or remove nodes. Adding a node already in another group transfers it; groups cannot nest. Ungroup or delete the group container to keep its nodes and connections. Duplicated tasks start ungrouped, renamed task IDs retain membership, and deleting the last member removes the empty group. Group changes support undo/redo and unsaved-change tracking.

Save Version retains names, colors, membership and layout in editor metadata. Earlier versions retain their own groups. Groups do not change workflow execution or connections; workflows without groups remain compatible.

## Unbounded control cycles (0.30.0)

Connect an ordinary control output back to a task input to repeat execution. Every completed visit produces a fresh activation with its own task run and attempt identity. Cycles need no exit, count or overall deadline; existing task timeouts still apply. Pause stops scheduling further work, Resume continues the active visit, and Cancel terminates the execution. Execution history labels repeated activations as visits. Explicit bounded Loop nodes retain their configured repeat count when an outer cycle visits them again.

A graph's first pass ignores feedback dependencies that have not executed. Later passes require fresh completion tokens from each active predecessor; inputs entering a cyclic region from an acyclic prefix remain available. Fork/join branches must complete each pass before the join repeats. Runs use the selected event entry and preserve its reachable dependencies. Cycles do not change the restrictions on mixing multi-successor graphs with explicit control structures.

Data bindings within a control cycle read the successful producer visits available when the consumer activates. A producer that has not yet executed contributes an absent value; consumer defaults, optional inputs and required-input errors apply normally. Prepared inputs retain source visit/attempt identities, and retry uses the same frozen preparation. Variables do not accumulate values across all past visits. Pure variable-expression cycles remain invalid.

Deploy matching 0.30.0 API, worker and chart versions. Startup applies the generated `AddWorkflowControlCycles` migration: nullable workflow activation state and visit identities for decision/parallel history, with unique identities per workflow/node/visit. Existing null-visit records retain their uniqueness and remain readable. Rollback to an older schema requires removing or archiving repeated decision/parallel rows before restoring the old workflow/node indexes.

## End nodes (0.31.0)

Add **End** from the workflow editor palette and connect a control route to it. End accepts incoming control connections, has no outgoing ports or worker settings, and launches no worker. The first route reaching End completes the entire workflow successfully; unlike an ordinary task join, a shared End does not wait for every incoming route. End works as a sequential terminal, a decision target, a task-graph terminal, or an explicit Parallel branch terminal instead of Join. Loop exits may lead to End; existing loop-body and other control-region restrictions remain.

Other running and queued tasks and armed event waits are canceled. No subsequent task, cycle visit or event continuation starts. Completed tasks keep their evidence, End records a successful run, and stopped parallel/loop executions retain canceled outcomes. Worker stop or log-capture failures remain durable cleanup work and retry after restart without changing the workflow's successful completion. An interrupted End arrival also recovers before scheduling more work.

Existing definitions without End keep their behavior. No database migration or new worker job protocol is required.
