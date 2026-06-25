# Kubernetes Deployment

The MVP includes a kustomize base under `deploy/kubernetes/base` that deploys:

- `formicae-api` ASP.NET Core API Deployment and ClusterIP Service
- `formicae-worker` CronJob for one-shot workflow advancement
- PostgreSQL Deployment, Service, and PVC for MVP persistence
- ConfigMap and Secret placeholders for runtime configuration
- ServiceAccount, Role, and RoleBinding for namespace-scoped Job/Pod/Log access

## Build Images

Build and push images with your registry tag:

```powershell
podman build -f src/hhnl.Formicae.Api/Dockerfile -t ghcr.io/anhaehne/hhnl-formicae-api:latest .
podman build -f src/hhnl.Formicae.Worker/Dockerfile -t ghcr.io/anhaehne/hhnl-formicae-worker:latest .
podman push ghcr.io/anhaehne/hhnl-formicae-api:latest
podman push ghcr.io/anhaehne/hhnl-formicae-worker:latest
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
- `GITHUB_TOKEN`

The API always applies EF Core migrations on startup when PostgreSQL persistence is configured. The Kubernetes ConfigMap sets `UseFakeAdapters=false` and `PersistenceMode=Postgres`, so deployments migrate automatically before serving traffic.

## Deploy

```powershell
kubectl apply -k deploy/kubernetes/base
kubectl rollout status deployment/formicae-postgres -n formicae
kubectl rollout status deployment/formicae-api -n formicae
kubectl get cronjob formicae-worker -n formicae
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
  --set image.tag=0.1.0
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

Create the runtime credentials Secret separately after the chart is installed. The API and worker reference this Secret optionally, so pods can start before it exists. Restart the API after creating or updating the Secret so the environment variables are reloaded.

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: formicae-runtime-secrets
  namespace: formicae
type: Opaque
stringData:
  LLM_API_KEY: "<replace-me>"
  GITHUB_TOKEN: "<replace-me>"
```

### OpenHands And Codex Subscription Access

The default MVP runner starts OpenHands with `openhands --headless --json` and configures the model through `LLM_MODEL`. For that OpenHands path, use the normal OpenHands/OpenAI API-key style runtime Secret, for example:

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: formicae-runtime-secrets
  namespace: formicae
type: Opaque
stringData:
  LLM_API_KEY: "<replace-me>"
  GITHUB_TOKEN: "<replace-me>"
```

OpenHands has a separate subscription-login path for Codex models. The OpenHands SDK documents `LLM.subscription_login(vendor="openai", model="gpt-5.2-codex")`, which performs a ChatGPT OAuth flow, caches credentials under `~/.openhands/auth/`, and reuses or refreshes that cache on later runs. That path is not the same as passing `LLM_API_KEY`.

For OpenHands Agent Canvas or ACP agents that run Codex, OpenHands documents Codex authentication through Codex CLI's cached login at `$HOME/.codex/auth.json`; subscription login takes priority over API keys when that cached login exists. In Kubernetes, restore that file before starting the Codex ACP agent. A common Secret shape is:

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: formicae-runtime-secrets
  namespace: formicae
type: Opaque
stringData:
  CODEX_AUTH_JSON_B64: "<base64-of-auth-json>"
  GITHUB_TOKEN: "<replace-me>"
```

The worker or agent container entrypoint must decode it before OpenHands starts the Codex ACP process:

```sh
mkdir -p "$HOME/.codex"
printf '%s' "$CODEX_AUTH_JSON_B64" | base64 -d > "$HOME/.codex/auth.json"
chmod 600 "$HOME/.codex/auth.json"
```

For ChatGPT Business or Enterprise workspaces, Codex also supports `CODEX_ACCESS_TOKEN` for trusted non-interactive automation. OpenHands does not use that environment variable as the documented Codex ACP credential directly; if you choose this route, the container must first convert the token into Codex CLI auth storage:

```sh
printf '%s' "$CODEX_ACCESS_TOKEN" | codex login --with-access-token
```

The Helm chart passes every key in `formicae-runtime-secrets` to both the API and worker containers. Future worker CronJob pods read the updated Secret when they start. Treat `LLM_API_KEY`, `CODEX_AUTH_JSON_B64`, `CODEX_ACCESS_TOKEN`, and `~/.codex/auth.json` as secrets. Use subscription-backed Codex auth only on trusted private runners, prefer finite expirations, and rotate credentials regularly. See the OpenHands docs for [LLM subscriptions](https://docs.openhands.dev/sdk/guides/llm-subscriptions) and [ACP agent authentication](https://docs.openhands.dev/openhands/usage/agent-canvas/acp-agents), and the Codex docs for [authentication](https://developers.openai.com/codex/auth), [environment variables](https://developers.openai.com/codex/environment-variables), and [access tokens](https://developers.openai.com/codex/enterprise/access-tokens).

Apply the Secret and restart the API:

```powershell
kubectl apply -f formicae-runtime-secrets.yaml
kubectl rollout restart deployment/formicae-api -n formicae
```

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

The current Kubernetes runner seam renders Job manifests in-process. The included RBAC already grants the API/worker service account namespace-scoped access needed when the runner is upgraded to create and watch Jobs directly.


