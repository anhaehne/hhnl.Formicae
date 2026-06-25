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

A Helm chart is available in this repository at `deploy/helm/formicae`. The chart deploys PostgreSQL by default through `postgres.enabled=true`.

Render the chart locally:

```powershell
helm template formicae deploy/helm/formicae --namespace formicae
```

Install or upgrade from a checkout of the repository:

```powershell
git clone https://github.com/anhaehne/hhnl.Formicae.git
cd hhnl.Formicae

helm upgrade --install formicae deploy/helm/formicae `
  --namespace formicae `
  --create-namespace `
  --set image.repositoryPrefix=anhaehne `
  --set image.tag=0.1.0 `
  --set secrets.postgresPassword='<replace-me>' `
  --set secrets.connectionString='Host=formicae-postgres;Port=5432;Database=formicae;Username=formicae;Password=<replace-me>'
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


