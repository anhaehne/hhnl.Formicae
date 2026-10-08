# Managed agent images — implementation plan

Status: proposed feature; planning only. No runtime implementation or release bump is included.

## 1. Save spec documentation

Save plan, scope, standards, references, and integration contract. Coordinate with the Agent task implementer before changing shared workflow contracts. No supplied visuals; reserve visuals/ for implementation screenshots.

## 2. Add the image catalog and durable build lifecycle

Use existing EF/service/API authorization patterns. Add ImageDefinition (name, description, optimistic revision, archive state), immutable ImageSourceRevision (Dockerfile, context descriptor, platform, worker base digest, source hash), and immutable ImageBuild attempts (source revision, resolved commit, state, job identity, timestamps, artifact digest, compatibility result, bounded logs/failure).

Expose `/api/images` CRUD/history and `/api/images/{id}/builds` create/list/detail/logs/cancel. Return 202 with build ID for asynchronous starts, 409 for stale edits, and structured validation errors. Viewers inspect; management administrators edit/build/cancel/archive. Retry creates a new attempt. Reuse installation-local catalog ownership; do not introduce multi-tenancy without an existing workspace entity.

MVP sources: inline Dockerfile with otherwise empty context, or Dockerfile/context paths in an already connected GitHub/Azure DevOps repository. Explain that inline mode cannot COPY local files. Resolve repository refs to immutable commits before queuing; reuse source-control adapters. Respect .dockerignore; reject absolute/escaping paths and bound Dockerfile/context sizes. Allow bounded non-secret build args and target stage. Archive uploads and build-secret UI are follow-ups.

Lifecycle: Queued → Preparing → Building → Publishing → Validating → Ready; active states can become Failed, Cancelled or TimedOut. Persist before submission, use deterministic job labels and reconciliation after API restarts, and prevent cancellation races from promoting a build to Ready. Ready requires successful push, verified digest, and worker compatibility. Rebuild failure retains the prior Ready artifact.

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

Integrate after the Agent task branch lands. This feature owns catalog/builds/registry/new image fields/picker; the other agent owns Agent task creation and existing environment propagation. Update product roadmap/tech stack when implementation adopts BuildKit/registry. Apply one minor bump against the then-current merged base, aligning Directory.Build.props, chart, values and release/deployment docs. This planning spec is not a release.

## 5. Validate and release

Add backend tests for immutable sources/builds, permissions, reconciliation, cancellation races, credential isolation, Ready gating, image precedence, digest pinning, archive/history and retry stability. Generate migrations with `dotnet ef migrations add AddManagedAgentImages`; review output. Add browser coverage for source editing, failures/logs, prepared-image selection and unchanged saved versions after rebuild, plus legacy environment regressions.

Run targeted tests, then `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj` and ClientApp `npm run build`. Run `./scripts/formicae-dev.sh prepare` once followed by start/status/logs/stop; inspect actual pages, console/network and screenshots with configured browser tools. Run ClientApp `npm run test:smoke`. Run `helm lint deploy/helm/formicae` and render bundled/external modes.

Extend/run `./scripts/run-k8s-e2e.sh`: real trivial compatible Dockerfile build, push, probe, fresh-node authenticated pull, Agent task launch and observed custom tool. Include invalid Dockerfile, bad trust/auth, storage failure, timeout/cancel, API restart and pinned retry. No model call required. Clean up jobs/storage/test cluster. Release only after BuildKit isolation and registry/node reachability pass; report exact commands/outcomes and tests added/removed/edited.

Current planning change: 0 tests added, 0 removed, 0 edited. Runtime checks deferred to implementation.
