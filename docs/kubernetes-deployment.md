# Kubernetes Deployment

The MVP includes a kustomize base under `deploy/kubernetes/base` that deploys:

- `formicae-api` ASP.NET Core API Deployment and ClusterIP Service
- PostgreSQL Deployment, Service, and PVC for MVP persistence
- ConfigMap and Secret placeholders for runtime configuration
- ServiceAccount, Role, and RoleBinding for namespace-scoped Job/Pod/Log access

The base labels its dedicated `formicae` namespace to enforce the privileged Pod Security level required by DinD while retaining baseline audit and warning signals. Do not deploy unrelated or untrusted workloads into that namespace.

## 0.8.1 upgrade from 0.7.4 or 0.7.5

Release 0.8.1 restores workflow loops and replaces the unapplied 0.8.0 loop migration. Before creating the loop-aware task-run index, startup maps legacy task kinds to step IDs in each workflow's pinned, immutable definition version. Workflows without a pinned version use the canonical MVP step IDs. Existing runs remain non-loop executions (`LoopIteration = null`); run IDs, retry state, outputs, timestamps, logs, and events are preserved. The current workflow step is backfilled as well.

Missing, ambiguous, or duplicate mappings abort the migration transaction and identify the workflow in the error. Investigate the pinned definition and historical rows before retrying; do not delete history to bypass the index. This replacement targets databases where the original `20260904150621_AddWorkflowLoops` migration never committed. A database that successfully applied that migration requires a separately reviewed upgrade path.

Deploy matching API and worker images and Helm chart version **0.15.0**. The migration is generated with EF tooling; its backfill SQL is inserted by `WorkflowMigrationDesignTimeServices` from `Persistence/Design/NormalizeLegacyTaskRuns.sql`, so migration files and snapshots do not require manual edits.

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

The release chain on `main` is `Test` → `Build container images` → `Deploy Formicae`. Images are published and the live Helm release is upgraded only after the complete test workflow succeeds. Build and deployment workflows also support explicit manual runs. The deployment uses the chart from the same commit, reuses existing Helm values, and updates `image.tag`, `config.jobRuntime=Kubernetes`, and `config.kubernetesJobsImage`, so runtime secrets and installation-specific settings stay in the cluster.

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

The API validates and normalizes control nodes into the existing task/iteration execution plan. Legacy v1alpha1/v1alpha2 versions remain readable and executable; editing converts only a draft and Save Version creates a new immutable v1alpha3 version. Task IDs and model overrides are retained. No migration is required. Nested loops, event waits, conditional looping and parallel execution are not part of this release.

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

Exclusive paths may converge or contain further decisions, loops or parallel groups. Decisions inside loop/parallel bodies and references to those body tasks' outputs are unsupported. A task-output source must precede the decision on every manual/trigger path; validation rejects ambiguous sources, unknown targets, body entry and outer cycles. Retry retains the chosen route. Automatic feedback-driven re-planning is disabled for decision-containing definitions; start a new workflow for changed inputs.

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