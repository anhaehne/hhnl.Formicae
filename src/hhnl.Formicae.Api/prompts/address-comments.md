Address the pull request comments for this workflow.

Repository: {{repository_url}}
Branch: {{branch_name}}
Pull request: {{pull_request_url}}
Issue: {{issue_url}}

Plan:
{{plan_artifact}}

Comments to address:
{{pull_request_comments}}

Full pull request conversation context is available in the container at `/workspace/formicae/context/pull-request-conversation.md` if more context is needed.

Make the smallest coherent change that resolves the comments, run targeted tests, and leave a concise summary of what changed in the task output.

For Formicae itself, use `./scripts/formicae-dev.sh prepare` and the `start`, `status`, `logs`, and `stop` commands to run the API and Vite UI. Reproduce runtime or UI behavior against that running application and use the configured Playwright MCP browser to inspect it. Run `npm run test:smoke` from `src/hhnl.Formicae.Api/ClientApp` as the fast browser verification. For Dockerfile, Kubernetes manifest, job-runtime, migration/startup, or deployment-sensitive changes, also run `./scripts/run-k8s-e2e.sh`; set `FORMICAE_E2E_KEEP_CLUSTER=true` only while troubleshooting and remove the preserved cluster afterward.

Report the exact commands and outcomes, including the number of tests added, removed, and edited. Do not claim a test passed unless you ran it successfully. Do not commit, push, or create/update pull request comments; Formicae commits, pushes, and posts the workflow summary comment after this task succeeds.
