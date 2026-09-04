# References for Formicae Agent Self-Testing

## Product Context

- `agent-os/product/mission.md` — Kubernetes-native orchestration and ephemeral task-specific agents.
- `agent-os/product/roadmap.md` — Static MVP workflow and future capabilities, environments, MCP, images, and tool-install customization.
- `agent-os/product/tech-stack.md` — React/Vite frontend, ASP.NET Core backend, Kubernetes runtime, and CLI-based agent harness.

## Existing Implementations

### Local development and application startup

- **Location:** `README.md`, `src/hhnl.Formicae.Api/`, and the frontend workspace.
- **Relevance:** Existing build/run commands, health behavior, development configuration, fake adapters, and UI startup are the basis of the fast harness.
- **Patterns to retain:** Deterministic local dependencies, explicit health checks, and no reliance on live providers for smoke testing.

### Runtime job contract and execution backends

- **Location:** `src/hhnl.Formicae.Infrastructure/RuntimeJob.cs`, `src/hhnl.Formicae.Infrastructure/Containers/ContainerJobRuntime.cs`, and `src/hhnl.Formicae.Infrastructure/Kubernetes/`.
- **Relevance:** These components carry job intent and render or launch container/Kubernetes workloads.
- **Patterns to retain:** Runtime-agnostic job specifications, bounded execution, isolated job resources, and testable manifest rendering.

### Worker startup and agent prompts

- **Location:** `src/hhnl.Formicae.Worker/Program.cs` and existing prompt templates used by implementation and address-comments runs.
- **Relevance:** Worker-local Codex configuration, DinD readiness, and verification instructions must be established before the agent starts.
- **Patterns to retain:** Subscription authentication separation, repository checkout boundaries, and task-kind-specific behavior.

### Kubernetes end-to-end harness

- **Location:** `scripts/run-k8s-e2e.ps1` and `tests/hhnl.Formicae.KubernetesE2ETests/`.
- **Relevance:** Existing kind lifecycle, temporary kubeconfig, tool validation, deployment, and cluster-preservation behavior provide the nested integration tier.
- **Patterns to retain:** Never change the caller's kubeconfig context; preserve the cluster only on explicit request; clean resources by default.

### Runtime and orchestration tests

- **Location:** `tests/hhnl.Formicae.Tests/WorkflowOrchestratorTests.cs`.
- **Relevance:** Existing tests cover `RuntimeJobSpec`, container job execution, Kubernetes manifest generation, and task-kind selection.
- **Patterns to retain:** Unit-test generated runtime specifications and manifests without requiring a live cluster.

## External Documentation

- [Codex MCP configuration](https://developers.openai.com/codex/mcp) — project/host configuration and STDIO MCP servers.
- [Playwright web server configuration](https://playwright.dev/docs/test-webserver) and [trace viewer](https://playwright.dev/docs/trace-viewer) — managed development servers and retained failure diagnostics.
- [Playwright MCP](https://github.com/microsoft/playwright-mcp) — headless Chromium, loopback origins, capabilities, and output-directory options.
- [Kubernetes native sidecars](https://kubernetes.io/docs/concepts/workloads/pods/sidecar-containers/) — restartable init containers, startup ordering, and Job completion semantics.
- [kind](https://kind.sigs.k8s.io/) and Docker-in-Docker documentation — nested cluster requirements and isolation boundaries.

## Future Compatibility

- GitHub issues #16, #17, #19, #20, #21, and #22 define the future migration path from the temporary global implementation-worker policy to configurable step capabilities and environments.
