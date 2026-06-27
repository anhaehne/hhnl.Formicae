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
- `KubernetesJobs:WorkerCallbackSecret` for optionally requiring worker callbacks to include `X-Formicae-Worker-Callback-Secret` when posting live agent messages.
- `GitHubWebhooks:Secret` for validating GitHub webhook deliveries at `/api/webhooks/github`. Configure GitHub to send JSON payloads for issues, issue comments, pull requests, pull request review comments, and pull request reviews so Formicae can wake the workflow loop and requeue completed PR workflows when new feedback arrives.
- `ManagementAuth:Enabled` controls authorization for mutating management APIs. It defaults to `false` for local development. Set `ManagementAuth:InviteCodeExpiration` to control invite lifetime and `ManagementAuth:BypassForLocalDevelopment=true` only for trusted development environments.

## GitHub Integrations

The Integrations page creates GitHub App configuration records, stores the GitHub App private key PEM, discovers the app slug from GitHub, generates the webhook secret, and displays the public webhook, OAuth callback, setup callback, and installation URLs. Formicae no longer reads a shared `GITHUB_TOKEN` from Kubernetes secrets; repository workflow access comes from GitHub App installation tokens minted with the configured private key. The Repositories page links to the GitHub App installation flow and lists repositories from the app installations, not from general public repository visibility, because workflow writes require the app installation to include that repository.

Connected repositories can be removed from the Repositories page. Removing an integration from the Integrations page also removes its connected repository records.

For a GitHub App, configure:

- User authorization callback URL: `<public Formicae URL>/api/auth/github/callback` for optional identity-provider login
- Setup URL: `<public Formicae URL>/api/auth/github/installations/callback` for GitHub App installation callbacks
- Webhook URL: `<public Formicae URL>/api/webhooks/github`
- Webhook content type: `application/json`
- Repository permissions: issues read/write, pull requests read/write, contents read/write, and metadata read-only
- Webhook events: issues, issue comments, pull requests, pull request reviews, and pull request review comments

GitHub can be enabled as an external identity provider from an integration detail page. The GitHub App user authorization callback URL must be `<public Formicae URL>/api/auth/github/callback`. External users are stored as ASP.NET Core Identity users and linked through `AspNetUserLogins` with provider `GitHub` and the GitHub numeric user id as the provider key. Future providers should link users through the same Identity external-login tables.

When enabling a GitHub identity provider, the UI first sends the current browser through GitHub login. The API only activates the provider after that callback has produced an authenticated Identity user, and grants that user the `AuthorizedUser` role in the same operation. If authentication fails, provider activation is rejected and the integration remains unchanged. When `ManagementAuth:Enabled=true`, mutating management endpoints require the Identity user to have `AuthorizedUser`. Authorized users create invite links from the Users page; invite codes are shown once, embedded into the invite link, stored only as hashes, expire according to `ManagementAuth:InviteCodeExpiration`, and are redeemed automatically after GitHub login when the link is used. Signed-in users without `AuthorizedUser` are sent to the Users page where they can only enter an invite code or log out.

## Architecture

- `hhnl.Formicae.Application` owns workflow state, interfaces, and orchestration logic.
- `hhnl.Formicae.Infrastructure` owns persistence and external adapters.
- `hhnl.Formicae.Api` owns HTTP endpoints, the distributed-lock-protected background orchestration loop, and worker message ingestion.
- `hhnl.Formicae.Worker` owns in-container agent execution and streams live agent output back to the API.
- `hhnl.Formicae.Tests` covers deterministic local workflow behavior and adapter contracts.
