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

The API applies EF Core migrations on startup when:

- `UseFakeAdapters=false`
- `ApplyDatabaseMigrations=true`

Both are set in the Kubernetes ConfigMap.

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

## Notes

The current Kubernetes runner seam renders Job manifests in-process. The included RBAC already grants the API/worker service account namespace-scoped access needed when the runner is upgraded to create and watch Jobs directly.
