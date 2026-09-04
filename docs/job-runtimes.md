# Job Runtimes

Formicae runs workflow agents through `IJobRuntime`. `WorkflowOrchestrator` still depends only on `IAgentRunner`; runtime selection is an infrastructure setting.

Commit-capable jobs have a separate execution policy:

```json
{
  "RuntimeJobs": {
    "ImplementationTimeoutSeconds": 3600,
    "ImplementationCheckpointGraceSeconds": 600
  }
}
```

`Implement` and `AddressComments` begin checkpoint handling when the grace window starts. Planning, authentication, and other lightweight jobs continue to use the runtime's normal 1,800-second timeout.

## Docker

Local non-fake execution defaults to the container runtime.

```json
{
  "UseFakeAdapters": false,
  "AgentMode": "OpenHands",
  "JobRuntime": "Container",
  "ContainerRuntime": {
    "Engine": "Docker",
    "Executable": "docker",
    "Image": "docker.io/limeray/hhnl-formicae-worker:latest",
    "Network": "",
    "WorkspaceRoot": "formicae-workspaces",
    "TimeoutSeconds": 1800,
    "DeleteFinishedContainers": true,
    "WorkerCallbackUrl": "http://host.docker.internal:5000/api/worker/agent-messages",
    "WorkerCallbackSecret": ""
  }
}
```

`WorkerCallbackUrl` must be reachable from the worker container. On Docker Desktop, `host.docker.internal` usually reaches the host API. On Linux, use a reachable host IP, a shared Docker network with the API container, or publish the API through another local address.

Context files and per-job secret files are written under `ContainerRuntime:WorkspaceRoot/<job-id>/` and mounted read-only into the worker container. Finished containers are removed when `DeleteFinishedContainers=true`; workspace files are left on disk for inspection and can be cleaned by the operator.

## Podman

```json
{
  "JobRuntime": "Container",
  "ContainerRuntime": {
    "Engine": "Podman",
    "Executable": "podman",
    "Image": "docker.io/limeray/hhnl-formicae-worker:latest",
    "Network": "",
    "WorkspaceRoot": "formicae-workspaces",
    "TimeoutSeconds": 1800,
    "DeleteFinishedContainers": true,
    "WorkerCallbackUrl": "http://host.containers.internal:5000/api/worker/agent-messages",
    "WorkerCallbackSecret": ""
  }
}
```

Podman commonly exposes the host as `host.containers.internal`, but exact behavior depends on the platform and Podman networking mode.

## Kubernetes

```json
{
  "JobRuntime": "Kubernetes",
  "KubernetesJobs": {
    "Namespace": "formicae",
    "Image": "docker.io/limeray/hhnl-formicae-worker:latest",
    "WorkspaceVolumeClaim": "formicae-workspaces",
    "TimeoutSeconds": 1800,
    "PollIntervalSeconds": 5,
    "DeleteFinishedJobs": false,
    "WorkerCallbackUrl": "http://formicae-api.formicae.svc.cluster.local/api/worker/agent-messages"
  }
}
```

The Helm chart and raw Kubernetes base manifest set `JobRuntime: Kubernetes` automatically and keep the existing `KubernetesJobs__*` settings. Helm also defaults the worker callback URL to the in-cluster API service.

## Agent development and browser tooling

`Implement` and `AddressComments` job specifications request browser automation and nested-container execution. Other task kinds stay lightweight. The worker image includes pinned Chromium, Playwright MCP, Docker CLI, kubectl, and kind versions. Before Codex starts, the worker creates its local `config.toml` without replacing subscription `auth.json`; Codex then starts Playwright MCP over STDIO in headless Chromium mode. Browser requests are limited to loopback origins and artifacts are written below the checked-out repository's ignored `test-results/` directory.

Use the fast application loop from the repository root:

```bash
./scripts/formicae-dev.sh prepare
./scripts/formicae-dev.sh start
./scripts/formicae-dev.sh status
./scripts/formicae-dev.sh logs
./scripts/formicae-dev.sh stop
```

The development application uses fake adapters and in-memory persistence. It does not receive production integration or database credentials.

## Nested kind

For Kubernetes jobs whose execution requirements include nested containers, the runtime adds a privileged Docker-in-Docker sidecar. The Docker Unix socket and graph storage use pod-local `emptyDir` volumes; the node container socket, host network, and host filesystem are never mounted. The worker pod does not mount a Kubernetes service-account token. The effective per-job timeout becomes `activeDeadlineSeconds`, and both worker and DinD containers receive explicit resource requests and limits.

Commit-capable jobs receive 3,600 seconds and begin checkpointing with 600 seconds remaining. The worker interrupts the active Codex turn, resumes the saved session with a finalization prompt, then commits and pushes the worktree. A checkpointed run remains failed and retryable so incomplete work cannot advance to pull-request creation. If resumption fails or the pod receives `SIGTERM`, the worker still attempts an emergency checkpoint on the workflow branch and reports its commit SHA in the workflow logs.

The privileged sidecar is a transitional global capability for all implementation repositories. It increases node-level risk even though Docker state is pod-local. Issues #16 through #22 will replace the global worker image, tools, MCP configuration, and privilege selection with per-step capabilities and reusable environment definitions.

Run the nested integration tier with:

```bash
./scripts/run-k8s-e2e.sh
```

Set `FORMICAE_E2E_KEEP_CLUSTER=true` while diagnosing a failure. The E2E harness keeps using `/tmp/formicae-e2e/kubeconfig`, so it cannot change the worker's outer Kubernetes context. Delete the preserved cluster when troubleshooting is complete.
