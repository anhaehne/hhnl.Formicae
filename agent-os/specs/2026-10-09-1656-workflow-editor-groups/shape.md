# Workflow editor groups — scope

Users select multiple nodes and create a group, edit its visible name, choose a preset background color, and drag its header to move members together. Each node belongs to one group; nested groups are outside scope. The inspector can add/remove members. Ungrouping and deleting the container preserve tasks and connections. Groups participate in undo/redo, arrangement, unsaved-change tracking and immutable saved versions.

Use existing React Flow parent nodes. Keep persisted task positions absolute and derive container bounds from members so moving an individual member or arranging the graph updates the group bounds. Groups remain editor metadata and never become runtime steps. Duplication creates an ungrouped task; deletion removes membership and empty groups; renaming a step updates membership.

Product alignment: visual organization for customizable workflows; no change to orchestration, worker images or integrations. No supplied visuals. Palette: gray, blue, green, yellow, orange, purple, pink.
