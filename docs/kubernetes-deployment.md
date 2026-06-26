# Kubernetes Deployment

The MVP includes a kustomize base under `deploy/kubernetes/base` that deploys:

- `formicae-api` ASP.NET Core API Deployment and ClusterIP Service
- PostgreSQL Deployment, Service, and PVC for MVP persistence
- ConfigMap and Secret placeholders for runtime configuration
- ServiceAccount, Role, and RoleBinding for namespace-scoped Job/Pod/Log access

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
  --set image.tag=0.3.18
```

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

### GitHub App Integrations

The management UI includes an Integrations page for GitHub App setup. Create an integration with the GitHub App client id and private key PEM. The client secret reference is optional and is only needed when the same integration is enabled as an identity provider. Formicae uses the private key to discover the app slug, build the install URL, list app installation repositories, and mint short-lived installation tokens for workflow operations.

Copy the generated values into the GitHub App settings:

- Callback URL: `https://<public-host>/api/auth/github/callback` for optional identity-provider login
- Setup URL: `https://<public-host>/api/auth/github/installations/callback` for installation callbacks
- Webhook URL: `https://<public-host>/api/webhooks/github`
- Webhook secret: generated by Formicae
- Content type: `application/json`
- Repository permissions: issues read/write, pull requests read/write, contents read/write, and metadata read-only
- Events: issues, issue comments, pull requests, pull request reviews, and pull request review comments

After creating the integration, open the Repositories page and use `Install GitHub App` to install or grant the app access. When GitHub redirects back to the setup callback, refresh the available repository list and add the installation repositories that Formicae should manage.

Repositories can be removed from the Repositories page. Removing an integration from the Integrations page removes the integration record and its connected repository records.

Do not store GitHub App private keys or client secrets in ConfigMaps. Store the private key only in Formicae's persisted integration record or a future secret-backed integration store. Store OAuth client secrets in your secret manager or Kubernetes Secret and keep only the secure reference in Formicae.

GitHub identity-provider mode can be enabled from the integration detail view by an authenticated user. Set `config.managementAuthEnabled=true` to require the `AuthorizedUser` role on mutating management APIs. The first authenticated external user who enables an identity provider is granted that role in the same operation. After that, authorized users create one-time invite codes for additional users; invite codes are stored only as hashes and expire according to `config.managementAuthInviteCodeExpiration`. External accounts are ASP.NET Core Identity users linked through `AspNetUserLogins`, with GitHub using provider `GitHub` and the GitHub numeric user id as the provider key.

By default, agent Jobs run the Formicae worker image published as `hhnl-formicae-worker:<version>`. The API creates one worker Job for each agent task and passes workflow metadata, prompt text, model settings, context mount path, auth mode, and `FORMICAE_WORKER_CALLBACK_URL` through environment variables. Set `secrets.workerCallbackSecret` to require worker callbacks to include `X-Formicae-Worker-Callback-Secret`; the API rejects callback posts when the configured secret is missing or mismatched. The worker runs OpenHands or Codex inside the worker container, streams supported JSON agent messages back to `/api/worker/agent-messages`, and still writes stdout/stderr to Kubernetes pod logs as the durable fallback. The worker image includes the .NET SDK, Git, Node.js 22, Python tooling, `uv`, and OpenHands so agent Jobs do not install those requirements at runtime. API-key OpenHands mode requires `LLM_API_KEY` and `LLM_MODEL`.

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
## Notes

The Kubernetes runner creates namespace-scoped `batch/v1` Jobs, waits for `Complete` or `Failed` status, and stores the rendered manifest plus pod logs in the task output. Finished Jobs are kept by default for diagnostics; set `config.kubernetesJobsDeleteFinishedJobs=true` to remove them after completion. To use a prebuilt CLI image, set `config.kubernetesJobsImage`, clear `config.openHandsBootstrapCommand`, and set `config.openHandsCommand` to the command your image exposes.
