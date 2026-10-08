# Scope and decisions

Multiple connections from an ordinary task output start independent tasks without waiting for sibling jobs. Every ordinary task input waits for all connected predecessors to succeed. Nested forks, joins and multiple terminal tasks are supported. Existing sequential and explicit control workflows remain compatible. Graph workflows currently support ordinary tasks and single-entry triggers; combining implicit parallel graphs with loop, decision or explicit parallel nodes is rejected during validation. Failed tasks block successors; retries retain successful siblings and immutable output provenance.

The user explicitly authorized proceeding without a plan-mode requirement. Reuse React Flow edges and the existing durable task and activation stores; no database migration is required.
