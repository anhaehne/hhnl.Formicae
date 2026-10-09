# Task graph connections

Version 0.21.0 supports multiple ordinary task output connections. Connect an output to several task inputs to run those tasks concurrently. A task with several incoming connections waits until every predecessor succeeds. Nested forks, joins and multiple terminal tasks are supported; the execution completes only when every reachable task succeeds.

In v1alpha3 definition JSON, `nextStepId` retains the first successor and `nextStepIds` contains additional successors. The editor preserves these connections when saving and reloading. Each connection can be disconnected separately. Named data output connections remain separate from execution dependencies. A custom-task input may bind to any ancestor whose completion is guaranteed by the dependency graph.

Tasks persist their own execution attempts, outputs and runtime identity. Restarting orchestration reattaches to existing jobs. A failed task blocks dependent work; retrying keeps successful sibling results. Planning inputs are assembled deterministically from the latest completed planning ancestors, and the entry planning context is frozen.

Task graphs support all ordinary task types and single-entry trigger nodes. Legacy graphs reject joins that depend on a task unreachable from their execution entry. From 0.22.0, explicit [start nodes](workflow-start-nodes.md) can converge on shared work: each run waits for predecessors active under its selected entry, while bindings still require an available producer from every entry that reaches the consumer. Cycles and combining these graphs with loop, decision or explicit parallel control nodes are rejected during validation. Existing explicit controls retain their behavior in workflows without multiple ordinary output connections.

## Visual groups

Ctrl/Cmd-click multiple nodes and choose **Group selected**. The group inspector edits the name shown on the canvas and its background color (gray, blue, green, yellow, orange, purple or pink). Drag the group header to move all members together; drag an individual member to change its position. Bounds follow the members, including after Arrange. From 0.29.1, group containers retain their measured dimensions during drag updates so their background and title remain visible throughout movement.

The Members checkboxes add or remove nodes. Adding a node already in another group transfers it; groups cannot nest. Ungroup or delete the group container to keep its nodes and connections. Duplicated tasks start ungrouped, renamed task IDs retain membership, and deleting the last member removes the empty group. Group changes support undo/redo and unsaved-change tracking.

Save Version retains names, colors, membership and layout in editor metadata. Earlier versions retain their own groups. Groups do not change workflow execution or connections; workflows without groups remain compatible.
