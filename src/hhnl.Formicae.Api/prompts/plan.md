Create a concise implementation plan for this GitHub issue.

Repository: {{repository_url}}
Base branch: {{base_branch}}
Issue: {{issue_url}}
Title: {{issue_title}}

Body:
{{issue_body}}

Existing plan, if this is a revision:
{{plan_artifact}}

Comments:
{{issue_comments}}

Return an implementation plan that another agent can execute directly. Focus on small, testable steps.
Before producing the plan, inspect the checked-out repository code so the plan is grounded in the actual implementation. Reference concrete files, types, components, or commands where useful.

If this is a revision of an existing plan because issue comments added feedback, include a short `## Changes from previous plan` section near the top with bullets that summarize what changed.
