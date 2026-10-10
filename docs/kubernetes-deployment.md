# Kubernetes Deployment

The MVP includes a kustomize base under `deploy/kubernetes/base` that deploys:

- `formicae-api` ASP.NET Core API Deployment and ClusterIP Service
- PostgreSQL Deployment, Service, and PVC for MVP persistence
- ConfigMap and Secret placeholders for runtime configuration
- ServiceAccount, Role, and RoleBinding for namespace-scoped Job/Pod/Log access

The base labels its dedicated `formicae` namespace to enforce the privileged Pod Security level required by DinD while retaining baseline audit and warning signals. Do not deploy unrelated or untrusted workloads into that namespace.

## 0.34.1 task-based test selection

Deploy matching **0.34.1** API/worker images and Helm chart. This patch adds development validation tooling and documentation; it introduces no application behavior, schema, runtime configuration or container-tool changes. `node scripts/test-selection.mjs --base origin/main --plan` explains conservative affected families; `--run` performs fast checks, native discovery and selected validation. `npm run test:smoke` now selects three basic health/navigation/error tests; `npm run test:browser` preserves the full browser suite, which CI continues running alongside full backend, Helm, worker-image and Kubernetes checks on the final merged SHA. See [task-based validation](testing.md).

Local verification: `node --test scripts/tests/test-selection.test.mjs` (22 passing); `node scripts/test-selection.mjs --family selector --run`; Release backend build; targeted `PersonaPromptComposerTests` (4 passing); frontend production build; native backend/browser family discovery; and `node scripts/test-selection.mjs --family smoke --list` with exact native test-list round-trip (3 smoke cases, no local servers). Final release requires successful exact-SHA CI, matching image/chart publication and automated deployment rollout/health checks. Tests added/removed/edited: **22 / 0 / 3** (three browser smoke tags; assertions unchanged). The first implementation keeps fixtures and concurrency unchanged; no speedup percentage is claimed.

## 0.34.0 GitHub issue title output

Deploy matching **0.34.0** API and worker images with the **0.34.0** Helm chart. GitHub Issue created exposes the required string `title` output alongside `issue` and `issueId`. It captures the exact webhook title and supports downstream typed bindings. Existing execution evidence derives title from its stored issue snapshot; no database migration or provider request is added. After rollout, connect Title to a string task input, save/reload the version, and verify event output evidence. Validation: targeted backend filter (`WorkflowEvent`, `IssueCommentTask`, `WorkflowVariable`) **62 passed**; `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --configuration Release` **1,241 passed**; `npm run build` passed; `npm run test:smoke -- --config=/tmp/formicae-title-smoke.config.ts issue-comments.spec.ts --timeout=60000` **2 passed**; `helm lint deploy/helm/formicae` and `helm template formicae deploy/helm/formicae --namespace formicae` passed. The temporary browser configuration selects installed Chromium 1243 and one worker. After merging main 0.33.0, the combined `WorkflowEvent|IssueCommentTask|WorkflowVariable|CreateBranch` backend filter passed **102** cases, the frontend build passed, and Helm lint passed. Tests added/removed/edited: **2 / 0 / 3** declarations (three added backend cases).

## 0.33.0 GitHub branch creation

Deploy matching **0.33.0** API and worker images with the **0.33.0** Helm chart. The workflow palette and task selector include **GitHub: Create branch**. Choose a connected GitHub repository, select an existing source branch, and enter a new branch name. The task uses the repository's existing GitHub App installation credentials and Contents permission; branch discovery uses Octokit's paginated branch API. No database migration or agent worker is required. Source commit and repository are pinned before creation and retained in execution/attempt evidence. Existing destination refs are never updated. A retry may recognize a branch created at the pinned commit after an interrupted provider response; a destination at another commit fails.

After rollout, verify repository access, source branch discovery, and the task in the workflow editor. API discovery enforces administrator permissions and connected repository scope. Local validation: `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-build --no-restore --configuration Release` passed all **1,273** cases; targeted branch task/API cases passed **35** cases and the affected workflow filter passed **85**. `npm run build`, `helm lint deploy/helm/formicae`, and `helm template formicae deploy/helm/formicae` passed. `npm run test:smoke -- --config=/tmp/formicae-branch-smoke.config.ts createBranch.spec.ts` passed all **3** new browser cases. The temporary config uses installed Chromium 1243, one worker, and the repository test suite. `npm run test:smoke -- --config=/tmp/formicae-branch-smoke.config.ts` passed all **111** browser cases. Playwright MCP live inspection captured the task fields, empty repository state, console and network evidence under ignored `test-results/branch-live/`; console errors: **0**. `./scripts/formicae-dev.sh prepare` restored/built dependencies, but browser download stalled; installed Chromium was used. Managed `start`, `status`, `logs` and `stop` are exercised. Tests added/removed/edited: **38 / 0 / 0** executable cases (**9** new backend declarations and **3** browser cases).

## 0.32.1 workflow graph appearance

Deploy matching **0.32.1** API and worker images with the **0.32.1** Helm chart. Definition-editor and execution-graph connections use native Bézier curvature 0.5 for both control and data lines. Normal and variable node backgrounds use 86% opacity; text and ports stay opaque. Existing layout, connection styling and workflow definitions are preserved. No database migration is added. After rollout, inspect backward control and data connections and grouped node backgrounds in both graph views. Local validation uses `npm run build`, `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --configuration Release`, `npm run test:smoke -- --config=/tmp/formicae-appearance-smoke.config.ts`, and `helm lint deploy/helm/formicae`. The temporary smoke configuration preserves the repository suite and selects installed Chromium 1243 with one worker. Live browser geometry checks compare control and data paths against native curvature 0.5 in both views and confirm 86% backgrounds with fully opaque node contents. Validation passed: frontend build, 1,238 repository tests, 108 browser tests, both live graph checks, Helm lint and chart rendering. Tests added/removed/edited: **0 / 0 / 0**.

## 0.32.0 typed decisions and node display

Deploy matching **0.32.0** API and worker images with the **0.32.0** Helm chart. New decisions infer boolean/string/number from a connected typed output or variable. Boolean uses True/False exits; string uses exact values plus Default; number uses ordered comparisons plus Default, with the first matching comparison selected. Saving an enabled version requires valid inputs and connected exits. Existing saved comparison decisions remain compatible. Custom tasks appear by their own names in Add Step; Workflow Management displays pinned visual groups and colors. No database migration is added. After rollout, verify each input type, an unmatched Default route, named custom-task insertion and a grouped execution graph. See [typed decisions](workflow-task-graphs.md#typed-decisions-0320).

## 0.31.0 workflow End nodes

Deploy matching **0.31.0** API and worker images with the **0.31.0** Helm chart. End completes a workflow on the first arriving route, cancels running/queued siblings and event waits, and preserves successful completion while failed worker cleanup retries. It is available in the palette with an input and no outgoing ports. Explicit Parallel branches may terminate at End instead of Join. No database migration is added. After rollout, connect a short route to End alongside a long worker and event wait; verify successful workflow completion, canceled siblings/waits and retained history. See [End node semantics](workflow-task-graphs.md#end-nodes-0310).

Verification commands and test counts are recorded in the [End node spec](../agent-os/specs/2026-10-09-2145-workflow-end-node/plan.md).

## 0.30.0 variable ports and unbounded control cycles

Deploy matching **0.30.0** API and worker images with the **0.30.0** Helm chart. Startup applies the generated `AddWorkflowControlCycles` migration for durable activations and repeated decision/parallel visit identities. Connect multiple GitHub Issue commented body outputs to one string variable; hover output ports to inspect types. Ordinary control back-edges and self-cycles execute without a repeat limit while preserving pause/resume/cancel and frozen retry inputs. Verify several distinct visits, pause/resume and cancellation after rollout. See [control cycle semantics and rollback considerations](workflow-task-graphs.md#unbounded-control-cycles-0300).

Local verification: all 1,191 .NET cases, all 102 single-worker browser cases and all 32 Kubernetes E2E cases passed. Frontend production build, Helm lint/render, managed API/UI lifecycle and Playwright MCP console/network/screenshot inspection passed. Tests added: 19 backend and 3 browser; existing backend cases edited: 7 across 5 declarations; removed: 0. Commands and evidence are in the [feature spec](../agent-os/specs/2026-10-09-1829-variable-ports-control-cycles/plan.md).

## 0.29.1 stable group dragging

Deploy matching **0.30.0** API and worker images with the **0.30.0** Helm chart. The editor retains measured group dimensions across drag updates so containers remain visible throughout movement. Group membership, individual member movement, undo/redo and saved layouts retain their existing behavior. After rollout, drag a group repeatedly and check that its background and title remain visible. The group-drag fix adds no database migration or runtime contract change.

## 0.29.0 typed data variables

Deploy matching **0.30.0** API and worker images with the **0.30.0** Helm chart. Variables are data-only capsule nodes with exact scalar types, ordered sources and Aggregate/First/Override operations. Boolean aggregation offers Any (OR) and All (AND). Control flow still determines when producers and consumers run. Prepared consumer evidence freezes values and the complete variable/source tree across restart and retry; execution history exposes this evidence without variable task runs. Existing versions and direct bindings remain compatible. No database migration or worker protocol change is required. After rollout, connect two string outputs to a variable, save/reload their order and verify the combined consumer input. See [typed variables](task-data-passing.md#typed-data-variables-0290).

Local validation: 1,172 .NET tests and 32 Kubernetes E2E cases passed. All 98 browser cases passed across the full suite and the targeted rerun of two timeouts; the rerun also passed the merged groups regression. Frontend build, Helm lint/render and live Playwright MCP inspection passed. Tests added: 30; removed: 0; existing tests edited: 2. Exact commands and outcomes are in the [variables feature spec](../agent-os/specs/2026-10-09-1713-typed-data-variables/plan.md).

## 0.28.0 Workflow editor groups

Deploy matching **0.30.0** API and worker images with the **0.30.0** Helm chart. Workflow definitions save named visual groups, preset background colors and membership alongside the existing node layout. Drag a group header to move its members together; use the group inspector to edit names, colors or membership. Group containers do not become runtime steps. Existing definitions without groups remain readable; no database migration or worker protocol change is added. After rollout, group two nodes, rename and recolor the group, drag it, save a version and reopen it to verify persistence. This release preserves the 0.27.0 workflow management layout and Manual Start behavior.

Local validation: all 1,144 .NET tests and all 96 single-worker browser smoke tests pass, along with the frontend build, Helm lint/render and live Playwright MCP console/network checks. Tests added: 3; removed: 0; existing tests edited: 0. Exact commands and environment details are recorded in the [groups feature spec](../agent-os/specs/2026-10-09-1656-workflow-editor-groups/plan.md).

## 0.27.0 workflow management layout

Deploy matching **0.30.0** API and worker images with the **0.30.0** Helm chart. Workflow Management places its bounded run browser beside execution detail on wide screens and stacks them on narrow screens. Search and presets stay visible; expanded filters and saved views remain available. Output ports occupy dedicated rows below node descriptions and above execution status/timing.

To start a workflow, open **Definitions**, select a definition and choose **Manual Start**. The side panel offers only that definition's enabled, manually startable saved versions, along with the existing issue, repository, branch and model fields. Save or discard unsaved editor changes before starting. Successful starts open the resulting execution. No database migration or worker protocol change is added. The baseline remains mandatory documentation; a separate baseline approval step is no longer required.

Local verification for this release:

- `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --configuration Release`: **1,143 passed**.
- `npm run build` from `src/hhnl.Formicae.Api/ClientApp`: **passed**, including refreshed tracked production assets.
- With `PLAYWRIGHT_BROWSERS_PATH=/tmp/formicae-layout-browsers`, `npm run test:smoke -- --workers=1`: **90/94 passed initially**. The four failures exposed the default zoom limit, an outdated saved-view test interaction and a version-selector label. After corrections, `npm run test:smoke -- --workers=1 tests/e2e/execution.spec.ts tests/e2e/smoke.spec.ts` passed **20/21**, including all original failures; the remaining failure exposed a palette node covering another node. After insertion spacing was corrected, `npm run test:smoke -- --workers=1 --grep 'trigger and loop nodes|contextual insertion preserves|parallel branch resizing|decision.*routes persist'` passed **5/5**. Test changes: **3 added, 0 removed, 2 edited**.
- `./scripts/formicae-dev.sh prepare` restored/built the solution and installed frontend dependencies; its Chromium installation needed the isolated browser cache above. `./scripts/formicae-dev.sh start`, `status`, `logs` and `stop` passed. Playwright MCP inspected Manual Start and the resulting execution, console/network activity, screenshots and a trace; no console errors occurred.
- `helm lint deploy/helm/formicae` and `git diff --check`: **passed**. This release changes frontend presentation and version tags; deployment-sensitive runtime configuration is unchanged.

## 0.26.3 AI Setup default model dropdown

Deploy matching **0.30.0** API and worker images with the **0.30.0** Helm chart. AI Setup selects its Default Model through a dropdown using the existing CLI discovery API. Saved selections remain available, including models absent from discovery; clearing the selection uses the runtime default. Supported saved configurations expose Discover / refresh models with progress and error feedback. Save new configurations or runtime/authentication changes before discovery. No database migration or worker protocol change is added. After rollout, discover models in AI Setup, save a selection, and reload to confirm it persists.

## 0.26.2 GitHub identity-provider restart notice

Deploy matching **0.30.0** API and worker images with the **0.30.0** Helm chart. GitHub login reads saved integration settings on every challenge and callback, so enabling the identity provider takes effect immediately. Integration list/detail responses ignore legacy persisted restart flags, and activation no longer creates a restart requirement. This release preserves the administrator restart endpoint, activation permissions and invitations. No database migration or worker protocol change is added. After rollout, refresh the integration detail page: an already-enabled GitHub provider must retain its checked state without the stale restart notice.

## 0.26.1 Codex reconnect device-code extraction

Deploy matching **0.30.0** API and worker images with the **0.30.0** Helm chart. Codex connect/reconnect decodes native worker log envelopes before removing terminal formatting and reads the server-provided one-time code from the login prompt without fixed group lengths. AI Setup displays the code and device URL with its Copy action. No database migration or worker protocol change is added.

## 0.26.0 agent task output correction

Deploy matching **0.30.0** API and worker images with the **0.30.0** Helm chart. Agent and Custom tasks with declared outputs validate final responses inside the worker and send up to two correction messages to the same native CLI conversation within the original timeout. Rebuild custom images from the matching worker for this behavior. No database migration is added. See [task output correction](task-data-passing.md#agent-and-custom-task-output-correction-0260).

## 0.25.0 issue event outputs and comments

Deploy matching **0.30.0** API and worker images and Helm chart. GitHub Issue created exposes full JSON-string issue evidence and a numeric Issue id; Add issue comment posts through the connected integration without a worker. These contracts use existing definition JSON and task-run evidence columns; no additional database migration is required. See [issue event outputs](workflow-start-nodes.md#github-issue-outputs-0250).

## 0.24.0 issue-comment waits

Deploy matching **0.30.0** API, worker and Helm chart versions. Startup applies the generated `AddWorkflowEventWaits` migration. Callable Issue commented nodes wait without a worker and resume the same execution once per activation. See [workflow event waits](workflow-event-waits.md).

## 0.23.0 managed agent images

Startup applies the generated managed-image catalog migration. Dockerfile builds and the bundled registry are opt-in; configure node-reachable TLS endpoints, scoped credentials and a compatible build pool before enabling them. See [managed agent images](managed-agent-images.md) for setup, retention and rollout checks.

## 0.22.0 workflow event nodes

Deploy matching **0.30.0** API and worker images with the **0.30.0** Helm chart. Manual Start, authenticated Webhook and integration-owned issue events are separate event nodes. Startup creates a new version of the existing simple built-in workflow with a manual Start event under the orchestration lock; pinned versions are preserved. Existing pinned definitions remain readable and editor drafts adapt legacy manual entrypoints when saving a new version. Start-node settings and selected-entry audit use existing persisted JSON records; no database migration or worker protocol change is required.

Webhook starts reference API configuration `WorkflowWebhooks:Secrets:<name>`. Provision each value through the API environment or mounted configuration, for example an environment variable `WorkflowWebhooks__Secrets__build-hook` backed by an operator-managed Kubernetes Secret key. Keep secret values outside workflow definitions, use HTTPS for external ingress, and retain the existing provider webhook secrets for GitHub/Gitea. See [workflow start nodes](workflow-start-nodes.md) for delivery and retry semantics.

## 0.22.0 Agent tasks

Deploy matching **0.30.0** API and worker images with the **0.30.0** Helm chart. Agent task definitions use the existing workflow JSON and agent execution protocol; no database migration is required. See [Agent tasks](agent-tasks.md).

## 0.21.0 task dependency graphs

Deploy matching **0.30.0** API and worker images with the **0.30.0** Helm chart. Ordinary task outputs accept multiple connections; independent successor workloads run concurrently and every task waits for all incoming tasks to succeed. Existing sequential definitions and explicit parallel controls remain compatible. No new database migration is required for the graph connections, which are stored in immutable definition JSON and reuse persisted task executions. See [task graph connections](workflow-task-graphs.md) for scope and configuration.

## 0.20.0 workflow and environment extensions

Deploy matching **0.30.0** API and worker images with the **0.30.0** Helm chart. Startup applies the generated nullable script exit-code migration for tasks and attempts. Existing versions remain readable. Custom execution images should extend the matching worker image so they implement the current worker protocol. Operator-managed step and image-pull Secrets must exist in the configured worker namespace; the API service account uses its existing Secret get permission to validate selected references before launch. Values are never exposed through the configuration API.

See [workflow environment extensions](workflow-environment-extensions.md) for scripts, capabilities, secret bindings, MCP configuration and tool installation.

## 0.19.0 execution operations

Deploy matching 0.19.0 API and worker images with the 0.19.0 Helm chart. Startup applies the generated migration for durable log cursors, retry-attempt history and execution controls while preserving existing workflow evidence. Database backups and the normal rollout verification remain part of the deployment process.

Finished worker jobs are acknowledged for deletion only after available runtime logs have been saved. Failed acknowledgment remains pending and is retried after restart. Cancellation terminates active jobs regardless of finished-job retention settings and remains pending while active pods are still present. Grant the existing namespace-scoped Job, Pod and Pod/log permissions to the API service account.

Permit long-running authenticated SSE responses through the ingress and disable response buffering for `/api/workflows/*/logs/stream`. Logs are stored in PostgreSQL rather than relying on pod retention. See [workflow execution operations](workflow-execution-operations.md) for investigation, controls and evidence limits.

## 0.8.1 upgrade from 0.7.4 or 0.7.5

Release 0.8.1 restores workflow loops and replaces the unapplied 0.8.0 loop migration. Before creating the loop-aware task-run index, startup maps legacy task kinds to step IDs in each workflow's pinned, immutable definition version. Workflows without a pinned version use the canonical MVP step IDs. Existing runs remain non-loop executions (`LoopIteration = null`); run IDs, retry state, outputs, timestamps, logs, and events are preserved. The current workflow step is backfilled as well.

Missing, ambiguous, or duplicate mappings abort the migration transaction and identify the workflow in the error. Investigate the pinned definition and historical rows before retrying; do not delete history to bypass the index. This replacement targets databases where the original `20260904150621_AddWorkflowLoops` migration never committed. A database that successfully applied that migration requires a separately reviewed upgrade path.

Deploy matching API and worker images and Helm chart version **0.30.0**. The migration is generated with EF tooling; its backfill SQL is inserted by `WorkflowMigrationDesignTimeServices` from `Persistence/Design/NormalizeLegacyTaskRuns.sql`, so migration files and snapshots do not require manual edits.

After a deployment failure, the GitHub Actions workflow collects resource status, descriptions, ordered events, and current and previous logs for each API container. For manual diagnostics with the deployment kubeconfig:

```bash
RELEASE_NAMESPACE=formicae RELEASE_NAME=formicae bash scripts/rollout-diagnostics.sh
```

The PostgreSQL tests exercise clean databases, legacy history, pinned custom definitions, invalid mappings, API startup, and unique-index enforcement. The Kubernetes suite seeds the pre-loop schema on PostgreSQL storage before starting the new API, then checks preserved history, loop execution, and diagnostics from a deliberately failed rollout.

## Build Images

Build and push images with your registry tag:

```powershell
podman build -f src/hhnl.Formicae.Api/Dockerfile -t docker.io/limeray/hhnl-formicae-api:latest .
podman push docker.io/limeray/hhnl-formicae-api:latest
```

If you use a different registry or tag, update `deploy/kubernetes/base/kustomization.yaml` or run:

```powershell
kubectl kustomize deploy/kubernetes/base
```

## Configure Secrets

`deploy/kubernetes/base/secret.example.yaml` contains placeholders. Replace all `replace-me` values before deploying, or create an equivalent `formicae-secrets` Secret through your secret manager.

Required keys:

- `ConnectionStrings__Formicae`
- `POSTGRES_DB`
- `POSTGRES_USER`
- `POSTGRES_PASSWORD`
- `LLM_API_KEY`

The API always applies EF Core migrations on startup when PostgreSQL persistence is configured. The Kubernetes ConfigMap sets `UseFakeAdapters=false` and `PersistenceMode=Postgres`, so deployments migrate automatically before serving traffic.

Agent jobs can receive generated context files through a per-job ConfigMap. Formicae sets the ConfigMap owner reference to the Kubernetes Job and also deletes the ConfigMap when `agentJobs.deleteFinishedJobs` removes the Job, so the mounted context is cleaned up with the Job lifecycle.

## Deploy

```powershell
kubectl apply -k deploy/kubernetes/base
kubectl rollout status deployment/formicae-postgres -n formicae
kubectl rollout status deployment/formicae-api -n formicae
```

Port-forward the API for a smoke test:

```powershell
kubectl port-forward service/formicae-api 8080:80 -n formicae
Invoke-RestMethod http://localhost:8080/healthz
```

Start a workflow:

```powershell
$body = @{
  issueUrl = "https://github.com/example/repo/issues/1"
  repositoryUrl = "https://github.com/example/repo"
  baseBranch = "main"
  model = "openhands/claude-sonnet-4"
} | ConvertTo-Json

Invoke-RestMethod -Method Post -Uri http://localhost:8080/api/workflows/github-issue -ContentType application/json -Body $body
```

## Helm Chart

A Helm chart is published from this repository as an index-based Helm repository. The chart deploys PostgreSQL by default through `postgres.enabled=true`.

Application images are published to Docker Hub by default as public images under `docker.io/limeray`. Configure the repository secret `DOCKERHUB_TOKEN` for the image publishing workflow; the workflow publishes as Docker Hub user `limeray`. Keep the Docker Hub repositories public so Kubernetes clusters can pull the chart defaults without an image pull secret.

Add the chart repository:

```powershell
helm repo add formicae https://anhaehne.github.io/hhnl.Formicae
helm repo update
```

Render the chart locally:

```powershell
helm template formicae formicae/formicae --namespace formicae
```

Install or upgrade from the Helm repository:

```powershell
helm upgrade --install formicae formicae/formicae `
  --namespace formicae `
  --create-namespace `
  --set image.repositoryPrefix=anhaehne `
  --set image.tag=0.3.28
```

## Automatic Cluster Deployment

The release chain on `main` is `Test` → `Build container images` → `Deploy Formicae`. Images are published and the live Helm release is upgraded only after the complete test workflow succeeds. Build and deployment workflows also support explicit manual runs. The deployment uses the chart from the same commit, applies new chart defaults while retaining existing installation Helm overrides, and updates `image.tag`, `config.jobRuntime=Kubernetes`, and `config.kubernetesJobsImage`, so runtime secrets and installation-specific settings stay in the cluster.

Run the deployment job on the already installed in-cluster GitHub Actions runner by setting the optional repository variable `FORMICAE_DEPLOY_RUNNER` to the runner label or runner scale-set name. If unset, the workflow targets `self-hosted`. Optional repository variables `FORMICAE_HELM_RELEASE` and `FORMICAE_HELM_NAMESPACE` default to `formicae`.

For Kubernetes access, prefer the in-cluster runner service account and grant it only the permissions required for Helm to manage the Formicae release. When the automatic deployment manages Pod Security Admission for DinD, the runner additionally needs `get` and `patch` on the exact agent-job Namespace. It does not need permission to create or bind cluster roles. If namespace Pod Security is managed separately by a cluster administrator, set `agentJobs.dind.configureNamespacePodSecurity=false`. If the runner is outside the cluster, provide a repository secret named `FORMICAE_KUBECONFIG_B64` containing a base64-encoded kubeconfig. Do not commit kubeconfigs, cluster API URLs, tokens, hostnames, webhook secrets, OAuth secrets, Codex auth files, or runtime connection strings.
By default, the chart installs bundled PostgreSQL and generates a database password in the chart-managed `formicae-secrets` Secret. On upgrades, the chart reuses the password already stored in that Secret. To use bundled PostgreSQL with a fixed password, set only `secrets.postgresPassword`:

```powershell
helm upgrade --install formicae formicae/formicae `
  --namespace formicae `
  --create-namespace `
  --set secrets.postgresPassword='<replace-me>'
```

To use an existing PostgreSQL instance instead of bundled PostgreSQL, disable bundled PostgreSQL and set only `secrets.connectionString`:

```powershell
helm upgrade --install formicae formicae/formicae `
  --namespace formicae `
  --create-namespace `
  --set postgres.enabled=false `
  --set secrets.connectionString='Host=<host>;Port=5432;Database=<database>;Username=<user>;Password=<password>'
```

Create runtime credentials separately after the chart is installed.

GitHub workflow access comes from the configured GitHub integration. Create the integration with the GitHub App client id and private key PEM, then use the Repositories page to install or grant the GitHub App access. Formicae mints GitHub App installation tokens for background issue, branch, pull request, reaction, and comment operations for connected repositories.

Gitea workflow access comes from a configured Gitea integration. Create the integration with a Gitea server URL, an access token, and a webhook secret. The token must have repository, issue, pull request, and content read/write access for the repositories Formicae will manage. Gitea repositories are connected manually from the Repositories page by entering the repository URL and default branch. The repository URL must belong to the configured Gitea server URL. GitHub repositories still require a GitHub App installation id; Gitea repositories do not.

For the default OpenHands CLI runner, create an `openhands-llm-api-key` Secret:

```powershell
kubectl create secret generic openhands-llm-api-key `
  --namespace formicae `
  --from-literal=LLM_API_KEY='<replace-me>'
```

### GitHub Webhooks

Formicae accepts GitHub webhooks at:

```text
POST /api/webhooks/github
```

In the GitHub repository webhook UI, use these settings:

- Content type: `application/json`
- Which events would you like to trigger this webhook?: `Let me select individual events`

Select these individual events:

- Issues
- Issue comments
- Pull requests
- Pull request review comments
- Pull request reviews

Do not choose `Just the push event`; Formicae does not use push events for issue planning, implementation, or PR comment handling. Do not choose `Send me everything`; unsupported deliveries are acknowledged but ignored.

For production, set a webhook secret and pass the same value to the chart:

```powershell
helm upgrade --install formicae formicae/formicae `
  --namespace formicae `
  --set secrets.githubWebhookSecret='<replace-me>'
```

When the secret is configured, Formicae verifies `X-Hub-Signature-256` before accepting the delivery. Supported webhook deliveries wake the distributed-lock-protected API workflow loop immediately; unsupported events are acknowledged but ignored. Pull request comment and review deliveries can requeue completed workflows when new feedback is added after a previous comment-addressing pass.

### Gitea Webhooks

Formicae accepts Gitea webhooks at:

```text
POST /api/webhooks/gitea
```

In the Gitea repository webhook UI, use these settings:

- Target URL: `https://<public-host>/api/webhooks/gitea`
- HTTP method: `POST`
- POST content type: `application/json`
- Secret: the webhook secret shown on the Formicae integration detail view

Enable these events:

- Issues
- Issue comments
- Pull requests
- Pull request review comments
- Pull request reviews

Formicae verifies Gitea webhook signatures using `X-Gitea-Signature`. It also accepts compatible `X-Hub-Signature-256` HMAC SHA-256 signatures. Unsupported deliveries are acknowledged but ignored. Pull request merge deliveries complete the workflow, and pull request comments or reviews can requeue completed workflows for another comment-addressing pass.

### GitHub App Integrations

The management UI includes an Integrations page for GitHub App setup. Create an integration with the GitHub App client id and private key PEM. The client secret reference is optional and is only needed when the same integration is enabled as an identity provider. Formicae uses the private key to discover the app slug, build the install URL, list app installation repositories, and mint short-lived installation tokens for workflow operations.

Copy the generated values into the GitHub App settings:

- User authorization callback URL: `https://<public-host>/api/auth/github/callback` for optional identity-provider login
- Setup URL: `https://<public-host>/api/auth/github/installations/callback` for installation callbacks
- Webhook URL: `https://<public-host>/api/webhooks/github`
- Webhook secret: generated by Formicae
- Content type: `application/json`
- Repository permissions: issues read/write, pull requests read/write, contents read/write, and metadata read-only
- Events: issues, issue comments, pull requests, pull request reviews, and pull request review comments

After creating the integration, open the Repositories page and use `Install GitHub App` to install or grant the app access. When GitHub redirects back to the setup callback, refresh the available repository list and add the installation repositories that Formicae should manage.

Repositories can be removed from the Repositories page. Removing an integration from the Integrations page removes the integration record and its connected repository records.

Do not store GitHub App private keys or client secrets in ConfigMaps. Store the private key only in Formicae's persisted integration record or a future secret-backed integration store. Store OAuth client secrets in your secret manager or Kubernetes Secret and keep only the secure reference in Formicae.

GitHub identity-provider mode is enabled from the integration detail view, but activation is login-first: the UI redirects through GitHub login, the callback creates or updates the Identity user, and the API only activates the provider after it can grant that user `ManagementAdmin`. If login fails, activation is rejected and the integration remains disabled. When an identity provider is enabled, anonymous users are redirected to provider login before the management UI is shown. Set `config.managementAuthEnabled=true` to require `WorkflowViewer` for workflow reads, `WorkflowOperator` for workflow commands, and `ManagementAdmin` for configuration/admin APIs. After bootstrap, admin users create one-time invite links on the Users page; invite codes are embedded in the link, stored only as hashes, redeemed automatically after provider login, grant `ManagementAdmin`, and expire according to `config.managementAuthInviteCodeExpiration`. Signed-in users without any management permission are redirected away from the management UI to a standalone invite-code page. External accounts are ASP.NET Core Identity users linked through `AspNetUserLogins`, with GitHub using provider `GitHub` and the GitHub numeric user id as the provider key.

### Gitea Integrations

The management UI includes a Gitea provider option on the Integrations page. Unlike GitHub, Gitea uses token-based setup rather than a GitHub App flow:

- Create a Gitea access token in the Gitea account that should own automation comments and pull requests.
- Grant token access to the repositories Formicae will manage.
- Create the Formicae Gitea integration with display name, server URL, access token, and optional webhook secret.
- Copy the generated webhook URL and secret into each Gitea repository webhook.
- Add repositories manually from the Repositories page.

Gitea reactions are currently treated as no-ops so workflows continue when the orchestration layer attempts to add reaction feedback.

By default, Kubernetes deployments run agent Jobs with the Formicae worker image published as `hhnl-formicae-worker:<version>`. The Helm chart sets `JobRuntime=Kubernetes` automatically. The API creates one worker Job for each agent task and passes workflow metadata, prompt text, model settings, context mount path, auth mode, and `FORMICAE_WORKER_CALLBACK_URL` through environment variables. Set `secrets.workerCallbackSecret` to require worker callbacks to include `X-Formicae-Worker-Callback-Secret`; the API rejects callback posts when the configured secret is missing or mismatched. The worker runs OpenHands or Codex inside the worker container, streams supported JSON agent messages back to `/api/worker/agent-messages`, and still writes stdout/stderr to Kubernetes pod logs as the durable fallback. The worker image includes the .NET SDK, Git, Node.js 22, Python tooling, `uv`, OpenHands, Chromium, Playwright MCP, Docker CLI, kubectl, and kind so agent Jobs do not install those requirements at runtime. API-key OpenHands mode requires `LLM_API_KEY` and `LLM_MODEL`.

Implementation and pull-request comment jobs request the development toolset. Their Kubernetes pods include a privileged Docker-in-Docker sidecar so the agent can run an isolated nested kind cluster. Docker uses only a pod-local Unix socket and `emptyDir` storage; Formicae does not mount the node's container socket, host network, host paths, or a Kubernetes service-account token into the worker pod. This is still privileged code on the Kubernetes node and must be used only where all connected repositories and agent prompts are trusted. Resource requests/limits, DinD image, and temporary storage size are configured under `agentJobs.resources` and `agentJobs.dind`.

When DinD is enabled, the chart defaults `agentJobs.dind.configureNamespacePodSecurity=true`. The repository's Helm deployment workflow reads that chart setting, creates the configured `config.kubernetesJobsNamespace` when its runner is allowed to do so, and labels it (or the release namespace when no override is set) with `pod-security.kubernetes.io/enforce=privileged` before `helm upgrade`; `audit` and `warn` remain at `baseline` so privileged workloads are visible to cluster operators. If the runner cannot create namespaces, a cluster administrator must create the target once. Direct chart consumers must apply the same labels before installation or set `agentJobs.dind.configureNamespacePodSecurity=false` when a cluster administrator manages an equivalent exemption. Because Pod Security Admission is namespace-scoped, every trusted workload in the target namespace can then request privileged settings.

DinD uses Kubernetes native sidecar semantics (`initContainers` with container-level `restartPolicy: Always`) so dockerd starts before the worker and does not block Job completion. This requires Kubernetes 1.29 or newer; Kubernetes 1.33 or newer is recommended because native sidecars are stable there.

For Docker, Podman, and Kubernetes runtime configuration examples, see [job-runtimes.md](job-runtimes.md).

Use the default API-key auth mode explicitly with:

```powershell
helm upgrade --install formicae formicae/formicae `
  --namespace formicae `
  --set config.openHandsAuthMethod=ApiKey
```

### AI Settings

The management UI includes an AI Settings panel for the active provider, model, auth method, Kubernetes API key Secret name, and optional endpoint/base URL. ConfigMap and Helm values such as `config.openHandsProvider`, `config.openHandsDefaultModel`, `config.openHandsEndpointUrl`, `config.openHandsAuthMethod`, and `config.openHandsLlmApiKeySecretName` are bootstrap defaults; after a value is saved in the UI, the non-secret settings are persisted in PostgreSQL.

API key values remain in Kubernetes Secrets and are never returned or shown in clear text. The UI stores and displays only the Secret name and whether a Secret name is configured.

Saved AI settings apply to newly queued or newly started workflow executions. Already-created and running agent Jobs keep the settings that were resolved when those Jobs were created.

This release supports one active AI configuration. Full OpenHands-style multi-profile switching is out of scope.

### Codex Subscription Auth

Codex subscription auth is different from an OpenAI API key. It is supported by Codex's own CLI/ACP agent, which reuses the `codex login` file at `~/.codex/auth.json`.

The default OpenHands headless command above does not use `~/.codex/auth.json` as an `LLM_API_KEY` replacement. Set the auth method to `CodexSubscription` when the selected agent command reads the Codex auth file directly, for example a Codex ACP based runner using:

```text
npx -y @agentclientprotocol/codex-acp
```

Create the Codex auth Secret:

1. On a trusted machine, sign in with Codex:

```powershell
codex login
```

2. Create a Kubernetes Secret from the Codex auth file:

```powershell
kubectl create secret generic formicae-codex-auth `
  --namespace formicae `
  --from-file=auth.json="$HOME/.codex/auth.json"
```

3. Enable Codex auth for API-triggered agent Jobs:

```powershell
helm upgrade --install formicae formicae/formicae `
  --namespace formicae `
  --create-namespace `
  --set config.openHandsAuthMethod=CodexSubscription `
  --set agentJobs.codexAuth.enabled=true
```

With `config.openHandsAuthMethod=CodexSubscription`, the worker runs `npx -y @openai/codex exec` instead of OpenHands API-key mode. For implementation and pull request comment-addressing tasks, the worker checks out the workflow branch with `GITHUB_TOKEN`, resets `origin` to the token-authenticated URL before pushing, commits any uncommitted changes, and pushes the branch after Codex exits. The chart configures agent Jobs created by Formicae to mount the Secret as `/root/.codex/auth.json`. If your agent image runs as a different user, override the `.codex` directory path:

```powershell
helm upgrade --install formicae formicae/formicae `
  --namespace formicae `
  --set config.openHandsAuthMethod=CodexSubscription `
  --set agentJobs.codexAuth.enabled=true `
  --set agentJobs.codexAuth.mountPath=/home/app/.codex
```

Treat `LLM_API_KEY`, `formicae-codex-auth`, and `~/.codex/auth.json` as secrets. Use subscription-backed Codex auth only on trusted private runners.

Codex auth is used by API-triggered agent Jobs. The API does not mount the auth file itself; new agent Jobs read the updated Secret when they start.

The chart defaults `image.tag` to the current chart app version. The GitHub Actions image workflow tags images with the .NET project version from `Directory.Build.props`, so chart `appVersion`, chart defaults, and pushed image tags should be kept aligned when releasing.

The 0.7.5 recovery release keeps lightweight agent jobs at the 1,800-second runtime default and gives `Implement` and `AddressComments` jobs a 3,600-second deadline with a 600-second checkpoint window. Override `config.runtimeJobsImplementationTimeoutSeconds` and `config.runtimeJobsImplementationCheckpointGraceSeconds` only when worker capacity or repository verification requires different limits. Checkpointed jobs remain failed and must be retried after reviewing the persisted branch and commit information.

## Kubernetes E2E Tests

Kubernetes E2E tests live in a separate project and are not part of the normal solution test path.

Run them with:

```powershell
scripts/run-k8s-e2e.ps1 -ContainerCli docker
```

For Podman-backed kind:

```powershell
scripts/run-k8s-e2e.ps1 -ContainerCli podman
```

The test harness verifies `kind`, `kubectl`, and the selected container CLI before starting. It creates or uses a local kind cluster named `formicae-e2e`, writes kubeconfig to a temp file, and passes that file to every `kubectl --kubeconfig ...` command. It does not call `kubectl config use-context` and does not write to the default kubeconfig.

Set `FORMICAE_E2E_KEEP_CLUSTER=true` or pass `-KeepCluster` to preserve the cluster for debugging.

Linux workers can use the equivalent entry point:

```bash
./scripts/run-k8s-e2e.sh
```

Image imports use a 15-minute deadline for each API/worker archive. Large worker images can exceed five minutes on a busy runner. Set `FORMICAE_E2E_IMAGE_LOAD_TIMEOUT_MINUTES` to an integer from 1 to 60 to adjust the bound; invalid values fail before creating a cluster. The fixture records archive size, image identity, loading phase and elapsed time, and verifies each imported image through the node's CRI before deployment. Timeout errors retain command output; setup diagnostics include containerd image availability and recent runtime logs. These settings affect local E2E setup only.

When a cluster is preserved inside an agent job, inspect it with `kubectl --kubeconfig /tmp/formicae-e2e/kubeconfig`, port-forward the API for Playwright MCP, and delete the cluster before the job finishes.
## Notes

The Kubernetes runner creates namespace-scoped `batch/v1` Jobs, waits for `Complete` or `Failed` status, and stores the rendered manifest plus pod logs in the task output. Finished Jobs are kept by default for diagnostics; set `config.kubernetesJobsDeleteFinishedJobs=true` to remove them after completion. To use a prebuilt CLI image, set `config.kubernetesJobsImage`, clear `config.openHandsBootstrapCommand`, and set `config.openHandsCommand` to the command your image exposes.

## 0.9.0 per-step model selection

Agent steps can select a saved AI configuration and a Codex model discovered through the CLI. Explicit step models override the workflow model; otherwise the workflow model is retained, followed by the selected configuration default. Existing definition versions remain compatible. No new database migration is required.

Model discovery runs a bounded worker job using the selected Codex credentials and the CLI app-server model/list protocol. It requires the same worker image and subscription authentication setup as execution. Other CLIs report discovery as unsupported; ACP execution is rejected explicitly. Discovery jobs do not check out repositories or request browser/nested-container capabilities. Refreshed Codex credentials use the existing authenticated worker callback.

In the workflow editor, select an agent step, choose its AI configuration, and use Discover / refresh models. Saved models remain visible when discovery fails or a model disappears from the catalog. The AgentSettingsResolved workflow event records the configuration and model passed to the CLI; an unspecified model is labeled CLI default.

## 0.9.1 Codex profile compatibility

Codex subscription profiles labeled ACP / Codex remain supported by the existing native Codex CLI execution path and CLI model discovery. Other ACP providers remain unsupported. This fixes the 0.9.0 filter that disabled existing Codex profiles in the step picker. No saved configuration or credential changes are required.

## 0.10.0 workflow control nodes

The editor saves formicae.workflow/v1alpha3 definitions. Add Step offers Task, Trigger and Loop nodes. Select a node to configure it; separate trigger/loop lists have been removed. Triggers start at their outgoing connection. Loop Body connects to its first task, the last task connects to Return, and Exit leads to the next task or loop. Manual start can reference a task or loop. Loop count, maximum iterations and timeout are configured on the loop node.

The API validates and normalizes control nodes into the existing task/iteration execution plan. Legacy v1alpha1/v1alpha2 versions remain readable and executable; editing converts only a draft and Save Version creates a new immutable v1alpha3 version. Task IDs and model overrides are retained. No migration is required. Nested loops and conditional looping remain outside this scope. Callable GitHub issue-comment waits are described in [event waits](workflow-event-waits.md).

## 0.11.0 workflow editor usability

The editor uses a viewport canvas with searchable workflow and version selection, contextual node creation, an inspector, undo/redo and explicit Save Version. Unsaved edits are protected when navigating away and preserved during refresh or failed saves; there is no autosave or crash recovery. Layout positions and viewport are stored as optional editor metadata in immutable definition JSON. Execution ignores this metadata, and no database migration is required.

The validation endpoint checks definitions without saving. Problems can focus their affected nodes. Arrange uses a separately loaded ELK layout engine; loading older definitions arranges them only when saved positions are missing. Existing task models, loops and triggers retain their behavior.

## 0.11.1 editor node visibility

New and duplicated nodes are selected immediately, but viewport focus waits for React Flow to finish measuring nodes instead of using a fixed timer. This prevents a delayed browser measurement from moving the canvas away from the new node. Incomplete loops and triggers still show validation problems while remaining editable. Measured dimensions are retained outside the saved draft and undo history, preventing nodes from hiding and remeasuring on each drag or validation update. Six browser regressions cover every supported node type with delayed measurements, validation, node visibility, field undo, navigation, and frame-level drag visibility checks.
`npm run test:smoke -- --workers=1`: 23 passed, including 6 added node-type regressions (0 removed, 0 existing tests edited). `npm run build` passed with the existing bundle-size advisory. The managed `formicae-dev.sh prepare/start/status/logs/stop` harness passed in the existing local worker image; both services became healthy and stopped cleanly.

## 0.11.2 editor confirmations

Confirmation dialogs use compact horizontal actions, clear headings, and explanatory text. Reconnecting uses Cancel/Replace; unsaved-change warnings retain Stay/Discard and distinguish the destructive action.
`npm run test:smoke -- --workers=1`: 23 passed. Two existing frontend tests updated for dialog labels, layout, keyboard focus, and Escape cancellation; 0 added, 0 removed. `npm run build` and the managed development harness passed. Both dialog screenshots were inspected.

## 0.12.0 parallel planning

Add a Parallel node and connect its 2–8 numbered branch outputs to separate Plan task chains. Connect each chain's last task to that node's Join input, then connect Next to the continuation. Branches run concurrently in separate worker jobs. The join waits for every branch, then combines terminal outputs in configured branch order. Each branch receives the group's saved entry plan or its own preceding task's output.

This release supports Plan tasks inside parallel branches. Implementation, pull-request creation and comment-addressing tasks remain sequential because they write to a shared Git branch. Nested parallel groups, groups inside loops, shared branch tasks and outside entry into a branch are rejected. Loops and triggers can precede or follow a group. Existing v1alpha1–3 definitions remain compatible; parallel settings are optional v1alpha3 fields.

Automatic plan revision from issue feedback remains available for sequential Plan steps. Start a new workflow run to revise a completed parallel group with changed inputs.

A failed branch stops its own remaining tasks while unaffected branches finish. The workflow reports the failing branch/task after all branches settle. Retry workflow retries every failed branch task; retry task retries only that task. Successful tasks remain intact. Retries preserve the group's cursor and entry snapshot and use fresh job identities; an interrupted launch reuses its saved identity to attach to the existing job.

The generated `AddWorkflowParallelExecutions` migration adds durable group activations and nullable task attempt IDs. Existing task rows retain null attempt IDs. Deploy matching API and worker images. For rollback, deploy the previous application/chart version and disable definitions containing Parallel nodes before starting new runs; the additive database columns/table may remain.

Verification: 375 backend tests, 26 browser tests, and 5 local Kubernetes E2E tests passed. The final three Parallel browser cases passed again after the layout correction. Frontend build, Helm lint and managed development lifecycle passed. Added 82 backend and 3 browser cases; edited 2 migration cases and 6 browser selectors; removed none.

## 0.13.0 workflow decisions

Decision nodes route execution through exactly one True or False output. Configure a literal, an allowed workflow field, or a completed ordinary task's output, then choose its scalar type and comparison. Strings compare ordinally and case-sensitively; numeric text uses invariant decimal parsing without thousands separators. Exists tests presence (empty text is present); null/missing values otherwise fail evaluation unless missing-value behavior is explicitly False. No arbitrary expressions or external condition requests run.

Exclusive paths may converge or contain further decisions, loops or parallel groups. Decisions inside loop/parallel bodies and references to those body tasks' outputs are unsupported. A task-output source must precede the decision on every manual/trigger path; validation rejects ambiguous sources, unknown targets and unsupported body entry. From 0.30.0 outer control cycles are supported with durable decision visits. Retry retains the chosen route. Automatic feedback-driven re-planning is disabled for decision-containing definitions; start a new workflow for changed inputs.

The generated `AddWorkflowDecisionExecutions` migration adds durable outcomes. Outcome insertion and cursor advancement share one transaction. Recovery reuses the recorded choice even if inputs have changed. Run details show the result, configured target, resolved execution entry, source input and evaluation time through the read-only decisions endpoint. These rows remain authoritative if supplemental event logging fails.

Deploy matching 0.13.0 API and worker images. Earlier definitions remain compatible. Rollback may retain the additive table, but definitions using Decision nodes require 0.13.0 or later and should not start under an older application.

Verification: 516 backend tests, 31 browser tests and 5 local Kubernetes E2E tests passed. Frontend build, Helm lint and managed API/UI lifecycle passed. Added 141 backend and 5 browser cases; no existing cases edited or removed.

## 0.14.0 agent personas

Operators can manage reusable personas and select a workflow default or an override for each Plan, Implement, and Address comments step. Default behavior preserves existing prompts. Persona instructions, tone, and operating constraints add prompt context without changing tool permissions, model selection, or execution types.

Each new workflow version records the current persona revision for its AI steps. Existing versions and retries retain that snapshot after catalog edits or deletion. The editor previews the saved and next-save revisions; disabled drafts can retain unresolved selections, while enabled versions require valid active selections when saved.

The generated AddPersonas migration adds the persona catalog table. Deploy matching 0.14.0 API and worker images. The additive table can remain during rollback, but workflows using custom persona snapshots should not start under an older application that cannot apply their instructions.
## 0.15.0 reusable custom tasks

Operators can define reusable agent tasks with prompt templates, typed inputs and a bounded execution timeout, then use them as Custom task nodes. Workflow versions snapshot task definitions and personas; each execution records its resolved inputs and rendered prompt before launch. Retries retain that context. Outputs and task identity are visible in run history.

Custom tasks execute in a fresh scratch workspace with their input context. They do not automatically check out a repository, receive repository tokens, provision browser/nested containers, commit, post comments, or open pull requests. Existing built-in tasks retain their behavior. Custom tasks support sequential execution and loop bodies; Parallel branches remain Plan-only.

Templates support declared input tokens and the documented workflow-field allowlist. Numeric inputs must round-trip exactly through browser JSON and stay within the safe integer magnitude; invalid types, missing required inputs, oversized prompts and oversized outputs fail clearly. Task deadlines terminate the agent process tree even when the scheduler is unavailable.

The generated AddCustomTasks migration adds the task catalog and nullable prepared-execution metadata. Deploy matching 0.15.0 API and worker images. The additive database fields may remain during rollback; workflows containing Custom task nodes require 0.15.0 or later and must not start under older applications.

## 0.16.0 reusable environment profiles

Operators can manage reusable environment profiles and select a workflow default. Saving a version captures the selected profile revision and configuration; catalog edits or deletion do not change existing versions. The immutable Default environment preserves platform defaults. Existing workflow documents remain compatible without a backfill.

The first supported setting is an optional maximum task runtime of 1–3600 seconds. It can shorten a task or runtime timeout and cannot increase it. Both container adapters and worker CLI paths enforce the capped deadline. Custom tasks remain non-checkpointing; capped Codex commit tasks retain checkpoint behavior only when positive checkpoint grace remains. A one-second cap still has a hard worker deadline. Jobs with no environment cap retain their existing behavior.

History records selected profile constraints, not inferred image or actual runtime settings. Custom images, tool installation, secrets, MCP and per-step overrides are separate features; this release rejects unsupported environment configuration instead of silently ignoring it.

Deploy matching 0.16.0 API and worker images. The generated environment-catalog migration is additive and leaves workflow histories untouched. Before rolling back to an older application, stop starting workflows that rely on environment timeout caps: older applications do not enforce these settings. The additive table may remain during rollback.

## 0.17.0 per-step environments

AI workflow steps can inherit the workflow environment, select another profile, or explicitly choose Default environment to use platform settings. An override replaces the workflow profile; timeout caps are not combined. Direct pull-request actions and control nodes cannot select an environment.

Saving a workflow captures each selected profile revision once and pins the resolved configuration on its AI nodes. Runtime selection and task history use those snapshots, including parallel branches, loop iterations and retries. Catalog edits or deletion do not change existing versions. Invalid references produce node-specific validation errors; disabled drafts retain unresolved selections. The inspector shows saved/current profile revisions and preserves later edits during delayed saves and undo.

Definitions from 0.16.0 and earlier keep their existing inheritance behavior. These fields use existing definition JSON storage, so no database migration or backfill is required. Deploy matching 0.17.0 API and worker images. Before rollback, stop starting workflows that rely on per-step overrides; older applications ignore those overrides.


## 0.18.0 task data passing

Deploy matching 0.18.0 API and worker images and Helm chart. The generated `AddTaskDataPassing` migration adds `custom_tasks.OutputsJson` with the default `[]` and nullable `task_runs.StructuredOutputsJson`. Existing catalog entries, pinned snapshots and free-text history remain compatible. Input bindings and frozen producer provenance use existing definition/preparation JSON storage.

Before rollback, stop creating or executing workflows using output schemas or bindings. Earlier versions ignore these new contract fields and cannot enforce their data dependencies. Retain structured output columns when rolling back application images so execution history is preserved.

See [the two-task example](task-data-passing.md) for configuration and execution rules.

Automatic deployment uses matching API, worker and Helm release versions (0.30.0 for variable ports and unbounded control cycles). Increase the semantic version for each release so Kubernetes receives a new image tag. Deployment validation checks the exact version-tagged API image reference and HTTP `/healthz` after rollout.
