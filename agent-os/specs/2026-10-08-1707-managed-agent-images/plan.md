# Managed agent images — implementation plan

Status: implemented on `clumsy-turkey`; Agent task branch merged at `ea3d2bb`. Final integration verification passed. The combined release stays at 0.22.0.

## 1. Save spec documentation

Save plan, scope, standards, references, and integration contract. Coordinate with the Agent task implementer before changing shared workflow contracts. No supplied visuals; reserve visuals/ for implementation screenshots.

## 2. Add the image catalog and durable build lifecycle

Use existing EF/service/API authorization patterns. Add ImageDefinition (name, description, optimistic revision, archive state), immutable ImageSourceRevision (Dockerfile, context descriptor, platform, worker base digest, source hash), and immutable ImageBuild attempts (source revision, resolved commit, state, job identity, timestamps, artifact digest, compatibility result, bounded logs/failure).

Expose `/api/images` CRUD/history and `/api/images/{id}/builds` create/list/detail/logs/cancel. Return 202 with build ID for asynchronous starts, 409 for stale edits, and structured validation errors. Viewers inspect; management administrators edit/build/cancel/archive. Retry creates a new attempt. Reuse installation-local catalog ownership; do not introduce multi-tenancy without an existing workspace entity.

MVP sources: inline Dockerfile with otherwise empty context, or Dockerfile/context paths in an already connected GitHub/Azure DevOps repository. Explain that inline mode cannot COPY local files. Resolve repository refs to immutable commits before queuing; reuse source-control adapters. Respect .dockerignore; reject absolute/escaping paths and bound Dockerfile/context sizes. Allow bounded non-secret build args and target stage. Archive uploads and build-secret UI are follow-ups.

Lifecycle implemented: Queued → Building (includes source preparation/publication) → Validating → Ready; active states can become Failed, Cancelled or TimedOut. Persist before submission, use deterministic job labels and reconciliation after API restarts, and prevent cancellation races from promoting a build to Ready. Ready requires successful push, verified digest, and worker compatibility. Rebuild failure retains the prior Ready artifact.

## 3. Build and store images in Kubernetes

Use the existing Kubernetes .NET client for separate ephemeral build Jobs and maintained BuildKit tooling with structured arguments. Pin the builder image. Begin with a separate rootless BuildKit instance per attempt and no shared writable cache. Prove node kernel/user-namespace/seccomp/AppArmor compatibility first: rootless does not automatically satisfy Kubernetes Restricted policies. Unsupported clusters require an explicit operator-configured compatible build pool or external BuildKit service; never silently grant privilege.

Give builds separate service accounts, no automatically mounted API token, no host mounts/node Docker socket, bounded CPU/memory/ephemeral storage, configurable deadline (proposal: 20 minutes) and queue concurrency (proposal: 2). Source-preparation Git credentials stay outside Dockerfile RUN execution. Build jobs never receive agent AI credentials, callback secrets or workspace PVCs. Prefer short-lived repository-scoped registry push tokens through BuildKit's credential provider; no credential configuration in the context. Dockerfiles are executable code: separate build workloads and configure egress to required sources/registries/package services.

Bundle a pinned CNCF Distribution registry in Helm by default and allow external OCI registry configuration. Start with PVC-backed storage, TLS, Secret-backed credentials and native token authorization with separate push/pull scopes. Check maintained .NET auth/client packages before writing integrations. Plain htpasswd alone does not implement repository-level push/pull separation. External mode renders no bundled registry workload.

Registry connectivity is a release gate: Kubernetes node container runtimes pull worker images. Provide a canonical HTTPS hostname reachable/resolvable from nodes and builder pods, with certificates trusted by nodes. A pod-only service DNS name or pod-mounted CA is insufficient. Require/configure endpoint, TLS Secret and routing in Helm; document node trust and test fresh-node pulls on supported pools.

Push `<registry>/formicae/<installation-id>/<image-id>:build-<build-id>` and persist/use its verified `@sha256:...` reference. Start with compatible images extending a supported, digest-pinned Formicae worker base. The launch contract is `dotnet hhnl.Formicae.Worker.dll` with the expected working directory, harness/tools and callback protocol. Add a deterministic `--check-runtime` worker probe if needed; run the pushed image without AI/repository credentials and without a model call before Ready. Record worker protocol/version and architecture compatibility and recheck compatibility with the deployed launcher after upgrades. Initial target: linux/amd64, matching current worker downloads.

Archive definitions without deleting pinned artifacts. Track saved workflow-version and active/historical run references before pruning. Keep referenced digests; manifest deletion/registry GC must coordinate with publication. Automatic GC can follow MVP, but manual deletion honors references from day one. Document metadata/storage backup and restore, credential rotation, storage-full and registry-unavailable failures.

## 4. Connect Manage Workspace and Agent tasks

Add Manage → Images: source editor, worker-base template, Build/Rebuild/Cancel, lifecycle/logs, compatibility failures, ready history and revision conflicts. Create does not auto-build; rebuild does not change task selection. Preserve the previous Ready result while building.

Add Execution image to `builtins.agent-task`: Inherit environment image, Use platform image, or Select prepared image/build. Only Ready compatible builds are newly selectable. Show name/source revision/build and saved digest. Add the same prepared-image picker to environment profiles. A node override changes image settings only; existing environment tools/MCP/timeouts/capabilities/secrets retain their semantics.

Extend the step with imageSelection/imageSnapshot (contracts.md). Resolve exact build ID and freeze server-trusted digest/settings/provenance at Save Version. Reject forged/inconsistent snapshots. Scheduling/resume/retry consumes the frozen snapshot without catalog/latest-build resolution. Missing blobs or incompatible worker protocols fail explicitly without fallback. Preserve original environment identity in history; record effective image separately.

Resolve effective settings before OpenHandsAgentRunner.BuildSpec and reuse EnvironmentImageSettings plus RuntimeJobSpec image/pull fields. ContainerJobRuntime currently rejects Kubernetes pull-secret names: supply a runtime-specific Docker credential path or reject unsupported private managed images before scheduling. Kubernetes is the initial production target.

Integrate after the Agent task branch lands. This feature owns catalog/builds/registry/new image fields/picker; the other agent owns Agent task creation and existing environment propagation. Update product roadmap/tech stack when implementation adopts BuildKit/registry. The merged Agent task branch provides the single minor bump to 0.22.0; Directory.Build.props, chart, values and release/deployment docs are aligned.

## 5. Validate and release

Add backend tests for immutable sources/builds, permissions, reconciliation, cancellation races, credential isolation, Ready gating, image precedence, digest pinning, archive/history and retry stability. Generate migrations with `dotnet ef migrations add AddManagedAgentImages`; review output. Add browser coverage for source editing, failures/logs, prepared-image selection and unchanged saved versions after rebuild, plus legacy environment regressions.

Run targeted tests, then `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj` and ClientApp `npm run build`. Run `./scripts/formicae-dev.sh prepare` once followed by start/status/logs/stop; inspect actual pages, console/network and screenshots with configured browser tools. Run ClientApp `npm run test:smoke`. Run `helm lint deploy/helm/formicae` and render bundled/external modes.

Extend/run `./scripts/run-k8s-e2e.sh`: real trivial compatible Dockerfile build, push, probe, fresh-node authenticated pull, Agent task launch and observed custom tool. Include invalid Dockerfile, bad trust/auth, storage failure, timeout/cancel, API restart and pinned retry. No model call required. Clean up jobs/storage/test cluster. Release only after BuildKit isolation and registry/node reachability pass; report exact commands/outcomes and tests added/removed/edited.

Implementation details and final verification are recorded below.

## Implementation and verification

The image catalog, immutable EF history/migration, asynchronous build reconciliation, rootless per-attempt BuildKit Jobs, registry-native scoped JWTs, Helm bundled/external modes, Manage Images page, prepared-image pickers and pinned workflow/runtime snapshots are implemented. Agent task branch `e55a52c` was merged; release versions remain aligned at 0.22.0. Added operator documentation: `docs/managed-agent-images.md`.

Verified commands so far:

- `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore`: 1,018 passed. After the registry base-image scope fix, the same command with `--filter ManagedImage`: 21 passed.
- `dotnet build hhnl.Formicae.slnx --no-restore`: passed with 0 warnings/errors. ClientApp `npm run build`: passed (existing bundle-size warning).
- `PLAYWRIGHT_BROWSERS_PATH=/tmp/formicae-image-browsers ./scripts/formicae-dev.sh prepare`, then `start`, `status`, `logs api`, `logs ui`, `stop`: passed. The first prepare attempt failed trying to write the read-only global browser directory; a task-local browser cache resolved it.
- ClientApp `PLAYWRIGHT_BROWSERS_PATH=/tmp/formicae-image-browsers npm run test:smoke`: 71/79 passed initially with six workers alongside Kubernetes verification; eight existing tests timed out. `npm run test:smoke -- --last-failed --workers=1`: seven passed; `npm run test:smoke -- extensions.spec.ts --grep "script editor preserves" --workers=1`: remaining test passed. All 79 passed across the initial run and reruns, including both new image tests.
- Repository Playwright MCP inspection (`node /tmp/formicae-image-mcp-check.cjs`): actual Images page loaded, 0 browser console errors/warnings, observed API requests returned 200; snapshot, network report and screenshot inspected. Evidence: `test-results/manual-images/mcp-inspection.json` and `visuals/managed-images.png`. The remote Paseo browser could not reach worker loopback; the local repository MCP package was used.
- `helm lint deploy/helm/formicae`: passed; `helm template` with enabled bundled registry/required Secrets and with external registry/push Secret: passed. `git diff --check`: passed.
- `docker build -f src/hhnl.Formicae.Worker/Dockerfile -t localhost/hhnl-formicae-worker:managed-images-verified .`, then `docker run --rm localhost/hhnl-formicae-worker:managed-images-verified --check-runtime`: passed. Probe checks Git, shell, Node, npx, Python and OpenHands without loading task credentials.

The first isolated `./scripts/run-k8s-e2e.sh` run passed 12/14. It found an existing test hardcoded to the legacy API tag and a real private-base pull scope bug. Existing job tests now use the configurable fixture API image. Build credentials now permit installation-local base-image pulls while restricting pushes to their own repository, covered by the native JWT authorization regression assertions. The corrected full Kubernetes rerun passed **14/14** (20 minutes 18 seconds), including build/push/digest/probe, authenticated node pull, custom tool execution, failed rebuild and retry on the original digest. Exact command: `FORMICAE_E2E_CLUSTER_NAME=formicae-images-e2e FORMICAE_E2E_API_IMAGE=localhost/hhnl-formicae-api:managed-images-e2e FORMICAE_E2E_WORKER_IMAGE=localhost/hhnl-formicae-worker:managed-images-verified FORMICAE_E2E_KEEP_CLUSTER=true ./scripts/run-k8s-e2e.sh`. The preserved test cluster was explicitly removed afterward with `kind delete cluster --name formicae-images-e2e --kubeconfig /tmp/formicae-images-e2e/kubeconfig`; the disposable registry was removed by test cleanup. Other agents’ clusters were left intact.

Test definitions for this feature: 17 added (14 backend methods, 2 browser tests, 1 Kubernetes scenario), 0 removed, 3 existing Kubernetes tests edited to honor the fixture image; fixture support updated. Backend theories expand to 21 cases. The merged Agent task branch adds 6 further test definitions (5 backend methods and 1 browser test).

Limits: production setup needs canonical node/pod-reachable HTTPS endpoints, CA trust, scoped Secrets and compatible dedicated rootless build nodes. Dockerfiles are administrator code. Automatic artifact GC, secret build arguments, context archives, multi-architecture images and the future Azure DevOps repository adapter remain outside this release. Registry retention is intentional so archived/pinned history remains runnable.

## Main integration and release 0.23.0

The user requested merging main, pushing and validating deployment, then explicitly required increasing release versions instead of reusing a tag. The combined managed-image release is 0.23.0, aligned across .NET, chart, values, worker template and deployment documentation. Historical 0.22.0 verification above describes pre-integration checks. Event nodes from main and managed-image fields were both preserved in merge conflicts; image validation remained in the definition validation hook. Browser image assertions now locate the Agent task by ID because main introduces explicit Start events. Deployment uses semantic version tags, applies new chart defaults while retaining installation overrides (`--reset-then-reuse-values`), checks the exact API image and probes HTTP health after rollout.

Merge checks: full backend suite 1,065 passed; targeted managed-image/event/start validation after preserving the validation hook: 60 passed. Full merged browser suite: `PLAYWRIGHT_BROWSERS_PATH=/tmp/formicae-image-browsers npm run test:smoke -- --workers=2` passed 85/85. Final frontend build and Helm lint passed. Remote release/deployment verification is pending the push. Integration edits 1 existing browser test; no tests added or removed by the merge-resolution/deployment changes.

Initial remote verification at `d444490`: chart publishing, worker image verification, 1,065 backend and 85 browser tests passed. Kubernetes E2E passed 13/14 (including managed images), but cancellation polled logs while Kubernetes reported the worker container “is not available”. The runtime now handles that startup response alongside existing ContainerCreating/PodInitializing responses. `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --filter Kubernetes_runner_returns_waiting_message_when_pod_logs_are_not_ready`: 2 passed. Full backend command above: 1,066 passed. Regression coverage edits 1 existing test definition, adds 1 theory case, removes 0 tests. Fresh remote Kubernetes and deployment validation remain pending. The prior deployment attempt was triggered by an older build that published 0.22.0; the new 0.23.0 image build must pass before rollout can be verified.
