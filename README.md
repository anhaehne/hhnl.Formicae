# hhnl.Formicae

hhnl.Formicae is a Kubernetes-native MVP for running agent workflows from DevOps work items. The first vertical slice accepts a GitHub issue URL, runs a static plan -> implement -> draft pull request workflow, and keeps external systems behind interfaces so agents can iterate locally with fake adapters.

## Current MVP

- ASP.NET Core API for manual workflow triggers and workflow reads.
- Background orchestration loop that advances queued workflows.
- PostgreSQL-ready EF Core persistence with an initial migration.
- OpenHands headless runner wired through a Kubernetes Job boundary.
- Fake work item, source control, agent, and store adapters enabled by default for local development.

## Install

Prerequisites:

- .NET 10 SDK
- Git
- PowerShell 7 or Windows PowerShell
- PostgreSQL only when running with `UseFakeAdapters=false`
- `kubectl`, `kind`, and either Docker or Podman for Kubernetes E2E tests
- A container registry login only when pushing deployment images`r`n- Helm when installing with the chart

Install common Windows tools with WinGet:

```powershell
winget install Microsoft.DotNet.SDK.10
winget install Git.Git
winget install Kubernetes.kubectl
winget install Kubernetes.kind
winget install RedHat.Podman`r`nwinget install Helm.Helm
```

Clone and restore:

```powershell
git clone https://github.com/anhaehne/hhnl.Formicae.git
cd hhnl.Formicae
dotnet restore hhnl.Formicae.slnx
```

Verify the local development setup:

```powershell
dotnet build hhnl.Formicae.slnx
dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj
```

## Local Development

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

## Kubernetes E2E

Kubernetes E2E tests run from a separate test project and are not part of the normal unit test path.

Run with Docker-backed kind:

```powershell
scripts/run-k8s-e2e.ps1 -ContainerCli docker
```

Run with Podman-backed kind:

```powershell
scripts/run-k8s-e2e.ps1 -ContainerCli podman
```

The E2E runner verifies `kind`, `kubectl`, and the selected container CLI before starting. It creates a local kind cluster using a temp kubeconfig, deploys the Kubernetes E2E overlay, and does not change the machine-wide kubectl context.

## Kubernetes Deployment

Deployment assets live in `deploy/kubernetes/base` and `deploy/helm/formicae`. They include Dockerfiles, kustomize manifests, a Helm chart, PostgreSQL, API/worker workloads, health probes, placeholder secrets, and RBAC.

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



