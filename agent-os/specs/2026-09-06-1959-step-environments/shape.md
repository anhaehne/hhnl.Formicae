# Per-step environments: scope and decisions

Issue #17 adds selection granularity to #19's existing reusable environment foundation. The user delegated independent planning/review and issue completion; these decisions use that authorization rather than asking repeated skill-template questions. The plan is independently approved by environment_plan and accepted by the parent. This artifact is planning only; implementation starts after #19 is committed.

- Null means inherit; explicit `default` opts out of the workflow profile; a custom ID replaces it.
- Overrides are selections, not additive cap layers. The selected cap still cannot enlarge the existing runtime/task timeout.
- Only external AI tasks select profiles. Controls and direct CreatePullRequest do not launch workers.
- The document default stays valid and pinned even when every node overrides it.
- One resolver/cache captures each distinct catalog ID once across document and steps; all AI nodes receive authoritative snapshots on new saves.
- Legacy nodes without step metadata inherit the #19 document snapshot. Explicit missing custom snapshots fail before launch.
- Shared runtime preparation, deadline mapper, catalog and profile audit remain single implementations.
- Saved previews derive from saved baseline; server metadata is excluded from undo/dirty comparison while selection remains editable.
- No new tables or migration expected; definitions already use JSON. No image/tool/MCP/capability/secret features are included.
- Visuals: none newly supplied. Reuse current workflow inspector and environment picker.
- Product alignment: task-specific agent behavior with observable ephemeral Kubernetes execution, retaining GitHub/Azure DevOps and existing CLI execution paths.
