# References

## Repository patterns

| Path | Use |
| --- | --- |
| src/hhnl.Formicae.Application/Workflows/EnvironmentModels.cs and EnvironmentDefinitions.cs | Typed image settings and immutable save-time environment resolution |
| src/hhnl.Formicae.Application/Workflows/WorkflowModels.cs and WorkflowOrchestrator.cs | Step serialization, environment propagation and audit |
| src/hhnl.Formicae.Infrastructure/OpenHands/OpenHandsAgentRunner.cs, RuntimeJob.cs, Kubernetes/KubernetesJobRunner.cs, Containers/ContainerJobRuntime.cs | Worker command/image/pull propagation, existing Kubernetes client, container pull limitation |
| src/hhnl.Formicae.Api/ClientApp/src/App.tsx, EnvironmentsPage.tsx, workflowEditor/EnvironmentPicker.tsx and Inspector.tsx | Manage navigation, catalog/history and per-task inspector |
| src/hhnl.Formicae.Worker/Dockerfile, deploy/helm/formicae/, scripts/formicae-dev.sh, scripts/run-k8s-e2e.sh, .github/workflows/test.yml | Base image/deployment and validation |

Other agent reference: `/home/paseo/.paseo/worktrees/0tjiw15i/sulky-fox/agent-os/specs/2026-10-08-1200-agent-task/` and its workflowEditor/AgentTaskSettings.tsx. Read-only; do not edit its branch.

Read product mission.md/roadmap.md/tech-stack.md and the existing 2026-10-06-1941-environment-extensions spec. This plan extends existing compatible worker-image support.

## Primary technology sources checked 2026-10-08

[BuildKit](https://github.com/moby/buildkit) provides Dockerfile builds and pushed image output. [Kubernetes examples](https://github.com/moby/buildkit/blob/master/examples/kubernetes/README.md) cover deployment/credential mounting. [Rootless requirements](https://github.com/moby/buildkit/blob/master/docs/rootless.md) describe kernel/container constraints and process-sandbox caveats.

[Distribution deployment](https://distribution.github.io/distribution/about/deploying/) and [overview](https://distribution.github.io/distribution/about/) cover maintained registry storage and TLS/authentication. [Token authentication](https://distribution.github.io/distribution/spec/auth/) describes scoped pull/push access.

Design inference: BuildKit plus bundled Distribution fits the self-hosted Kubernetes runtime. Node routing/trust, isolation, retention and proposed DTOs remain Formicae integration responsibilities.
