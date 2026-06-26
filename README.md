# hhnl.Formicae

hhnl.Formicae is a Kubernetes-native MVP for running agent workflows from DevOps work items. The first vertical slice accepts a GitHub issue URL, runs a static plan -> implement -> pull request workflow, and keeps external systems behind interfaces so agents can iterate locally with fake adapters.

## Current MVP

- ASP.NET Core API for manual workflow triggers and workflow reads.
- Management UI for workflows, AI settings, and GitHub integration setup.
- Background orchestration loop that advances queued workflows.
- PostgreSQL-ready EF Core persistence with an initial migration.
- Worker-based agent execution: the API schedules Kubernetes worker Jobs, workers run OpenHands/Codex inside the worker container, and live agent messages stream back to the API.
- Fake work item, source control, agent, and store adapters enabled by default for local development.

## Install

Prerequisites:

- .NET 10 SDK
- Git
- Node.js 22 or newer for the management UI
- PowerShell 7 or Windows PowerShell
- PostgreSQL only when running with `UseFakeAdapters=false`
- `kubectl`, `kind`, and either Docker or Podman for Kubernetes E2E tests
- A container registry login only when pushing deployment images
- Helm when installing with the chart

Install common Windows tools with WinGet:

```powershell
winget install Microsoft.DotNet.SDK.10
winget install Git.Git
winget install Kubernetes.kubectl
winget install Kubernetes.kind
winget install RedHat.Podman
winget install Helm.Helm
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
dotnet run --project src/hhnl.Formicae.Api/hhnl.Formicae.Api.csproj --urls http://localhost:5000
```

Run the management UI in a second shell:

```powershell
cd src/hhnl.Formicae.Api/ClientApp
npm install
npm run dev
```

Open the Vite URL printed by the dev server, usually `http://localhost:5173`. The Vite dev server proxies `/api` requests to the ASP.NET Core API at `http://localhost:5000`. Fake adapters are enabled by default, so the UI can start and inspect workflows locally without GitHub, Azure DevOps, Kubernetes, PostgreSQL, or OpenHands credentials.

Build the UI into the API static files:

```powershell
cd src/hhnl.Formicae.Api/ClientApp
npm run build
```

The Vite production build writes to `src/hhnl.Formicae.Api/wwwroot`. Run this build before `dotnet publish` or container image builds when UI changes need to be bundled into the API static files.

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

The API hosts the background orchestration loop. It wakes automatically on supported GitHub webhooks and also polls periodically while the process is running.

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

Deployment assets live in `deploy/kubernetes/base` and `deploy/helm/formicae`. They include Dockerfiles, kustomize manifests, a Helm chart, PostgreSQL, the API workload, health probes, placeholder secrets, and RBAC.

See [docs/kubernetes-deployment.md](docs/kubernetes-deployment.md) for build, configure, deploy, and smoke-test commands.

## Configuration

`UseFakeAdapters` defaults to `true`. Set it to `false` to use PostgreSQL and the real integration seams.
When PostgreSQL persistence is configured, the API applies EF Core migrations automatically on startup.

Important settings:

- `ConnectionStrings:Formicae` for PostgreSQL.
- `OpenHands:DefaultModel` for the default OpenHands model.
- `KubernetesJobs:Image` for the Formicae worker image used by agent Jobs.
- `GitHubWebhooks:Secret` for validating GitHub webhook deliveries at `/api/webhooks/github`. Configure GitHub to send JSON payloads for issues, issue comments, pull requests, pull request review comments, and pull request reviews so Formicae can wake the workflow loop and requeue completed PR workflows when new feedback arrives.

## GitHub Integrations

The Integrations page creates GitHub App configuration records, generates the webhook secret, displays the public webhook and callback URLs, and stores connected repositories after the GitHub App is installed or granted access. Client secrets are represented by a secure reference and are not returned by the API as clear text. Formicae no longer reads a shared `GITHUB_TOKEN` from Kubernetes secrets; GitHub access comes from configured integrations. After a user authenticates GitHub for an integration, Formicae stores that OAuth token with the integration and uses it for background issue, branch, pull request, reaction, and comment operations on connected repositories.

For a GitHub App, configure:

- Callback URL: `<public Formicae URL>/api/auth/external-callback`
- Webhook URL: `<public Formicae URL>/api/webhooks/github`
- Webhook content type: `application/json`
- Repository permissions: issues, pull requests, contents, and metadata
- Webhook events: issues, issue comments, pull requests, pull request reviews, and pull request review comments

GitHub can be enabled as an external identity provider from an integration detail page. Enabling currently persists `requiresRestart=true`; restart the API so the GitHub OAuth scheme uses the persisted integration. Once any integration has identity-provider mode enabled, Formicae requires authenticated access for app/API routes except health checks, static assets, auth endpoints, and webhooks.

## Architecture

- `hhnl.Formicae.Application` owns workflow state, interfaces, and orchestration logic.
- `hhnl.Formicae.Infrastructure` owns persistence and external adapters.
- `hhnl.Formicae.Api` owns HTTP endpoints, the distributed-lock-protected background orchestration loop, and worker message ingestion.
- `hhnl.Formicae.Worker` owns in-container agent execution and streams live agent output back to the API.
- `hhnl.Formicae.Tests` covers deterministic local workflow behavior and adapter contracts.
