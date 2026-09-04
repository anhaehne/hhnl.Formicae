Implement the approved plan for this workflow.

Repository: {{repository_url}}
Branch: {{branch_name}}
Issue: {{issue_url}}

Plan:
{{plan_artifact}}

Make the smallest coherent change, run targeted tests, and leave a concise summary of changed files and verification.

For Formicae itself, use `./scripts/formicae-dev.sh prepare` and the `start`, `status`, `logs`, and `stop` commands to run the API and Vite UI. Reproduce runtime or UI behavior against that running application and use the configured Playwright MCP browser to inspect it. Run `npm run test:smoke` from `src/hhnl.Formicae.Api/ClientApp` as the fast browser verification. For Dockerfile, Kubernetes manifest, job-runtime, migration/startup, or deployment-sensitive changes, also run `./scripts/run-k8s-e2e.sh`; set `FORMICAE_E2E_KEEP_CLUSTER=true` only while troubleshooting and remove the preserved cluster afterward.

Report the exact commands and outcomes, including the number of tests added, removed, and edited. Do not claim a test passed unless you ran it successfully.
