# Workflow end node

Scope: user-approved End node completes the entire workflow and stops parallel executions and waiting triggers. First arrival wins; End has no outgoing ports, worker settings or worker job. Terminal workflow state prevents continuations; durable cleanup retries across restart. Retain successful End evidence and canceled sibling evidence. Existing workflow restrictions remain, including Plan-only workers in explicit parallel branch bodies; a branch may terminate at End instead of Join.

No visuals supplied. Follow existing editor node presentation and runtime cancellation. Product direction and technology choices remain unchanged.
