# Plan: Formicae Agent Self-Testing

## Objective

Give Formicae implementation agents a fast, repeatable way to build, run, inspect, and troubleshoot Formicae itself, plus an optional nested-kind tier for changes that require container or Kubernetes behavior.

## Task 1: Save Spec Documentation

- Create this spec folder with the approved plan, shaping decisions, applicable standards, reference implementations, and an empty visuals placeholder.

## Task 2: Create the Fast Development Harness

- Add a Linux-friendly command with `prepare`, `start`, `status`, `logs`, and `stop` operations.
- Run the API at `127.0.0.1:5000` with development settings, fake adapters, in-memory persistence, disabled discovery, and no real credentials.
- Run Vite at `127.0.0.1:5173`, wait for `/healthz` and the UI before reporting readiness, and reliably terminate both processes.
- Keep process state under `/tmp` and ignored logs, screenshots, and traces under `test-results/`.

## Task 3: Add Browser Testing and Interactive Troubleshooting

- Add pinned Playwright Test and Playwright MCP dependencies.
- Configure Chromium-only, headless Playwright tests to manage both web servers and retain screenshots and traces on failure.
- Add smoke coverage for API health/version, UI loading/navigation, and unexpected page or console errors.
- Bake Chromium and Playwright MCP into the worker image so agent runs do not download them at runtime.
- Generate worker-local Codex configuration that runs Playwright MCP over STDIO, stores artifacts in the repository, and restricts browser traffic to loopback origins.
- Keep subscription `auth.json` independent from generated MCP configuration.

## Task 4: Provide Nested kind to Implementation Workers

- Extend `RuntimeJobSpec` with explicit `BrowserAutomation` and `NestedContainerRuntime` execution requirements.
- Assign both requirements to `Implement` and `AddressComments`; keep planning and authentication work lightweight.
- For Kubernetes jobs requiring nested containers, attach a privileged DinD sidecar using a pod-local Unix socket and isolated `emptyDir` storage. Never mount the node Docker socket.
- Pin and install Docker CLI, kubectl, and kind in the worker image; wait for DinD readiness before starting Codex.
- Disable service-account token mounting, avoid host networking and host mounts, retain the existing execution deadline, set resource limits, and let pod deletion clean nested Docker data.
- Add a Linux entry point for the existing Kubernetes E2E suite and honor `FORMICAE_E2E_KEEP_CLUSTER=true` for post-failure inspection, port-forwarding, and browser troubleshooting.
- Preserve the suite's temporary kubeconfig boundary so nested tests cannot mutate the outer cluster context.

## Task 5: Teach Agents How to Verify Changes

- Update `AGENTS.md` and the implementation/address-comments prompts with exact harness commands and tier-selection guidance.
- Require targeted tests plus the fast verification suite before completion.
- Require runtime and UI changes to be reproduced against the running application and inspected through Playwright MCP.
- Require nested kind only for Dockerfile, Kubernetes manifest, job-runtime, migration/startup, or deployment-sensitive changes.
- Keep enforcement agent-owned: agents report commands, results, and relevant evidence; the worker adds no Formicae-specific post-agent gate.
- Require pull request summaries to state test counts added, removed, and edited.

## Task 6: Add CI, Documentation, and Versioning

- Add deterministic CI jobs for .NET tests, the frontend production build, Playwright smoke tests, worker-image/toolchain checks, and the Kubernetes E2E suite.
- Do not invoke a paid model in CI.
- Document local and agent workflows, troubleshooting, artifact locations, cleanup, privilege implications, and verification-tier selection.
- Validate one real Formicae implementation run manually: start the application, interact with it, and exercise nested kind without production credentials or outer-cluster mutation.
- Apply one minor version bump from `0.6.2` to `0.7.0`, keeping project, Helm chart, values, and release/deployment documentation aligned.

## Interfaces and Future Compatibility

- `RuntimeJobSpec` gains execution requirements that container and Kubernetes runtimes interpret without repository-specific behavior.
- Kubernetes job configuration gains a global nested-container toggle and pinned sidecar/tool versions, temporarily enabled for all implementation repositories.
- Leave a clear migration seam for issues #16 (per-step capability authorization), #17 and #19 (environment resolution), #20 (configurable MCP), #21 (custom images), and #22 (tool installation).
- Do not implement environment management or persist capabilities in this change.

## Acceptance Criteria

- Unit tests verify requirement selection and Kubernetes pod generation, including DinD sidecar, socket volumes, security settings, and lightweight non-implementation jobs.
- Harness tests cover readiness, duplicate starts, early server failure, log retrieval, and idempotent cleanup.
- The three browser smoke scenarios pass locally and in the worker image; failures retain useful screenshots and traces.
- The complete .NET suite, frontend production build, and Kubernetes E2E suite pass.
- Nested kind starts through the pod-local DinD socket, deploys the branch-built API, responds to health checks, permits browser interaction through port-forwarding, and cleans up unless preservation is requested.
- One real-agent manual acceptance run succeeds without production credentials or outer-cluster mutations.
