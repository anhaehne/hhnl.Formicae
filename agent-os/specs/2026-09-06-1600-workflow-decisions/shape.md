# Scope and decisions

Issue #11 follows the reviewed parallel execution delivery. The user authorized autonomous planning with subagent review, implementation, main commits, pushes, deployment verification, and solution comments closing completed issues.

The reviewed plan supports binary exclusive decisions in the outer workflow DAG, including convergence and nested decisions. Decisions inside repeated/concurrent regions are deferred. Conditions use typed, allowlisted sources and operators with explicit missing-data behavior. Persist the selected outcome and cursor atomically; history comes from durable outcome rows.

Proposed JSON contract: `decision: { condition: { source, valueType, operator, reference?, value?, compareTo?, missingValue }, trueStepId, falseStepId }`. `value` is the literal source value; `compareTo` is the comparison operand. Both use JSON scalar values. Fields and enum strings follow the lower-camel spelling in the plan. Ordinary `nextStepId` is absent for a Decision.

No parallel branch writes, arbitrary expression evaluation, JSON-path language, or new external service is introduced.

Retry must preserve exclusive routing: an evaluation failure retries the current Decision without queuing an unrelated historical failed task. In decision-containing workflows, task retry is restricted to the current execution cursor or a task owned by its active Parallel group. Feedback-driven rewind must not enter unselected or already traversed decision arms; retain sequential behavior for definitions without decisions.
