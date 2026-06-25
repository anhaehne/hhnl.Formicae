# hhnl.Formicae

hhnl.Formicae is a Kubernetes-native MVP for running agent workflows from DevOps work items. The first vertical slice accepts a GitHub issue URL, runs a static plan -> implement -> draft pull request workflow, and keeps external systems behind interfaces so agents can iterate locally with fake adapters.

## Current MVP

- ASP.NET Core API for manual workflow triggers and workflow reads.
- Background orchestration loop that advances queued workflows.
- PostgreSQL-ready EF Core persistence with an initial migration.
- OpenHands headless runner wired through a Kubernetes Job boundary.
- Fake work item, source control, agent, and store adapters enabled by default for local development.

## Local Setup

Prerequisites:

- .NET 10 SDK
- PostgreSQL only when `UseFakeAdapters` is `false`

Build and test:

```powershell
dotnet build hhnl.Formicae.slnx
dotnet test hhnl.Formicae.slnx
```

Run the API with fake adapters:

```powershell
dotnet run --project src/hhnl.Formicae.Api/hhnl.Formicae.Api.csproj
```

Start a workflow:

```powershell
$body = @{
  issueUrl = "https://github.com/example/repo/issues/1"
  repositoryUrl = "https://github.com/example/repo"
  baseBranch = "main"
  model = "test-model"
} | ConvertTo-Json

Invoke-RestMethod -Method Post -Uri http://localhost:5000/api/workflows/github-issue -ContentType application/json -Body $body
```

Run one orchestration tick from the worker:

```powershell
dotnet run --project src/hhnl.Formicae.Worker/hhnl.Formicae.Worker.csproj
```


## Kubernetes Deployment

Deployment assets live in `deploy/kubernetes/base` and include Dockerfiles, kustomize manifests, PostgreSQL, API/worker workloads, health probes, placeholder secrets, and RBAC.

See [docs/kubernetes-deployment.md](docs/kubernetes-deployment.md) for build, configure, deploy, and smoke-test commands.
## Configuration

`UseFakeAdapters` defaults to `true`. Set it to `false` to use PostgreSQL and the real integration seams.

Important settings:

- `ConnectionStrings:Formicae` for PostgreSQL.
- `OpenHands:DefaultModel` for the default OpenHands model.
- `KubernetesJobs:Image` for the OpenHands-capable job image.

## Architecture

- `hhnl.Formicae.Application` owns workflow state, interfaces, and orchestration logic.
- `hhnl.Formicae.Infrastructure` owns persistence and external adapters.
- `hhnl.Formicae.Api` owns HTTP endpoints and the background orchestration loop.
- `hhnl.Formicae.Worker` owns one-shot workflow advancement for scheduled or agent-driven execution.
- `hhnl.Formicae.Tests` covers deterministic local workflow behavior and adapter contracts.

