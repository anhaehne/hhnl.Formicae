# Typed data variables

Approved baseline: `typed-data-variables`, product owner, 2026-10-09, conversation “Approved”.

Variables are compact data nodes separate from executable steps. Exact scalar types, ordered sources, Aggregate (append/sum/Any/All), First and Override. Resolve during consumer preparation, preserve complete provenance, and retain control-path/loop guarantees. No arrival-order selection or mutable global state. Missing values remain absent.

No supplied visuals. Reuse React Flow handles and node rendering, existing JSON definition version storage and prepared execution evidence. No new external client or database columns are required.
