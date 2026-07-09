# Job Runtimes

Formicae runs workflow agents through `IJobRuntime`. `WorkflowOrchestrator` still depends only on `IAgentRunner`; runtime selection is an infrastructure setting.

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
