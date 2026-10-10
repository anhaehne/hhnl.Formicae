# Capability contracts and lifecycle exercises

Status: **Design traces, not executed acceptance tests**. These contracts define the proposed first runtime release. The JSON below illustrates the envelope; it is not an API accepted by the current application.

## Bundle envelope and snapshot

```json
{
  "name": "docker",
  "revision": 1,
  "envelopeSchemaVersion": 1,
  "rendererVersion": "kubernetes-bundles/v1",
  "supportedRuntimes": ["Kubernetes"],
  "supportedTaskKinds": ["agent", "script"],
  "parameters": {},
  "requirements": { "privilegedContainers": ["daemon"] },
  "contributions": {
    "initContainers": [],
    "volumes": [],
    "workerBindings": [],
    "podSettings": [],
    "resources": []
  }
}
```

Each contribution contains native Kubernetes JSON with a stable logical name. Init contributions distinguish one-shot init from restartable sidecar; a dependency DAG orders their native init-container list. Companion entries contain a manifest plus logical ID, dependency/readiness/failure rules and cleanup ownership. Pod settings claim individual JSON paths; ancestor/descendant overlap also conflicts. Resources are namespaced in the runtime namespace and use deployment-allowlisted GVKs. API discovery resolves plural names and verifies scope instead of guessing from kind names.

Parameters have bounded primitive/object/array schemas, required fields and allowed values. Bindings substitute complete JSON values at declared locations, not arbitrary YAML text or control-plane shell commands. Names and DNS endpoints come from logical resource IDs; parameters cannot replace namespace/owner/identity. Secret bindings carry only Secret name/key references and declared consumers. Status selectors read bounded allowed facts for readiness/failure; arbitrary CR status is excluded from evidence and worker bindings in v1.

An environment selection stores a local capability name, complete trusted bundle snapshot, parameters and whether it is an inherited default. Saved workflow resolution pins these, including built-in Docker settings. Old snapshots retain current behavior. For newly configured environments, null step selection inherits explicit infrastructure defaults and existing agent/script defaults; `[]` provisions no optional capabilities. Explicit selection enables only selected bundles/tools/MCP/browser plus declared bundle dependencies, with dependency closure visible in preview. Unknown names, unavailable definitions, incompatible task/runtime combinations, conflicting snapshots and cycles fail before Kubernetes calls. Archived definitions remain usable by pinned versions.

## Composition and platform ownership

Formicae owns worker image/command, protocol/context/auth mounts, credential/callback aliases, Job policy/deadlines, namespace, labels/identity, owner references and service-account token policy. Bundles request approved scheduling/runtime-class/security settings through claimed paths but cannot replace platform controls. Privileged-container permission belongs to deployment policy and must match the bundle declaration. Kubernetes admission rejection retains its concrete cause.

Namespace container/volume/resource names by bundle and attempt. Detect env-alias and worker mount-path overlap, port/protocol conflicts in the shared pod network, duplicate resource identities and conflicting settings before launch. No last-wins merging. Init ordering accounts for dependencies and native startup probes. Shared filesystem access requires an explicit shared volume mounted by every consumer; the worker's private checkout directory is not automatically shared.

## Durable lifecycle

Persist an immutable plan/hash and ledger under execution/node/visit/attempt identity, then reconcile idempotently. Each ledger item contains GVK/GVR, namespace/name, expected owner/attempt labels/hash, observed UID, phase and bounded sanitized failure facts. No credential values are persisted in plan metadata. Record intent before create, actual UID afterward. After an uncertain response or 409, GET and verify identity metadata and expected spec using canonical comparison that tolerates Kubernetes defaulting/admission; do not trust a self-reported hash alone. Compare the worker plan too. Delete with UID preconditions to preserve replaced/foreign objects.

| Phase | Required behavior |
| --- | --- |
| Provisioning / Waiting | Persist intent, create suspended worker Job, create companions under its UID, evaluate readiness/dependencies and recover uncertain calls. |
| Running | Patch unsuspend/remaining Job deadline with UID/resourceVersion preconditions; native sidecar probes gate execution. Worker computes remaining time from fixed template deadline. |
| Capturing | Retain bounded worker/init/sidecar logs and permitted status/failure evidence; preserve worker result and script stdout separately. |
| CleanupPending | Fence launches, resolve uncertain operations, stop/confirm worker termination, capture companion evidence, delete companions in reverse dependency order, then remove Job anchor as applicable. |
| Cleaned | Confirm owned active resources and contracted operator descendants are gone; retain diagnostics and ledger tombstones. |

Attempt deadline starts before provisioning and never resets on replay, controller restart or Job unsuspension. Persist the absolute timestamp and bind it into the immutable worker template before creating its suspended Job. At worker startup compute remaining time from that timestamp, including native-sidecar startup delay, and clamp checkpoint grace accordingly. For a 600-second budget and 240 seconds of companion provisioning, 360 seconds remain before any extra startup delay. The unsuspend patch also sets remaining Job activeDeadlineSeconds with UID/resourceVersion preconditions; it cannot change worker env/command. If no budget remains, do not unsuspend. Legacy relative-timeout jobs retain their compatibility path. Define/test clock assumptions; checkpoint/final reporting must not extend execution work beyond the task deadline.

Readiness/failure checks consume that budget, with observed-generation checks where available. Cancellation persists a terminal/no-launch fence before any stop/delete operation. Serialize runtime mutations against that fence, track in-flight operation identities and resolve uncertain create/unsuspend responses across restart. Fence stale reconciliation from recreating/starting resources; recheck durable state after delayed operation completion and immediately stop any raced launch. Stop and confirm worker termination while retaining companion diagnostics before deleting those companions. Do not delete the Job ownership anchor before evidence capture, because that can trigger companion garbage collection. A single 404 cannot establish Cleaned while a recorded create/unsuspend outcome is still uncertain. New attempts get fresh names/data; they can wait for prior cleanup when isolation/quota policy requires it. Shared state spanning attempts is outside v1. Preserve existing scheduling-boundary pause semantics.

Retained finished Jobs do not trigger dependent garbage collection, so explicit companion cleanup is mandatory. Referenced external Secrets/PVCs/Services/RuntimeClasses are never adopted/deleted. Verify contracted operator cascade and storage reclamation; never delete a shared/external object or strip an unknown finalizer. Cleanup has bounded calls/backoff and durable pending state. Execution outcome and cleanup outcome remain separate. Workers cannot start until required companion readiness succeeds; a Service selecting the gated worker cannot require ready endpoints before startup.

## Exercise A: built-in Docker bundle

Freeze the current DinD image, CPU/memory resources, storage limit, privilege requirement and startup probe. Add socket and graph-storage `emptyDir` volumes, a native restartable-init daemon, worker socket mount and `DOCKER_HOST`. The Docker client must be present in the worker image or selected through tool provisioning.

1. **Compose/start:** Docker or legacy nested-containers alias produces one daemon/two volumes/binding. Alias plus explicit Docker is rejected. Operator-disabled privilege fails preflight; admission rejection is retained. The daemon's `docker info` startup probe gates the worker.
2. **Run/finish:** agent or explicitly configured script launches nested containers using the pod-local socket. Worker completion finishes its Job while the native sidecar stops. Daemon termination exit code does not overwrite the worker result; unexpected health failures remain visible.
3. **Replay/retry:** after create-response loss, verify/attach to the same attempt's suspended/running Job. New attempts use fresh Jobs/socket/storage; no old Docker graph state is reused.
4. **Cancel/fail:** cancel during startup or work, or fail a probe; terminate the owned Job/pod and await termination. Capture daemon logs/startup evidence even if worker logs are absent. Reconciliation cannot extend the worker budget.
5. **Cleanup:** pod-local volumes disappear with pod deletion. Retained terminal Job diagnostics do not keep a daemon running. Service restart resumes cleanup from the ledger and preserves unrelated Jobs/Secrets.

## Exercise B: operator-backed PostgreSQL companion

Use a single-instance CloudNativePG `postgresql.cnpg.io/v1` Cluster as a concrete fixture, not a database-specific integration. Require allowlisted permissions, a pinned installed operator/CRD, supported database image and disposable storage with verified reclamation. Use a declared pre-existing basic-auth Secret for the application user; its username matches the declared database owner. Formicae never owns the external Secret. Operator descendants are covered by the bundle cleanup contract.

Native manifest sets `instances: 1`, bounded storage, `bootstrap.initdb` database/owner/Secret reference and deterministic Cluster name. Worker bindings use the operator's `<cluster>-rw` Service DNS and declared Secret keys. Explicitly wait for Cluster Ready and required keys within the original deadline; confirm semantics against the pinned operator. No status-derived credentials or immutable template patch are needed.

1. **Compose/start:** validate parameters/discovery/secrets/policy, persist desired ledger, create suspended worker Job, create owned Cluster, await readiness then unsuspend worker with fixed DNS/Secret bindings.
2. **Run/finish:** script or agent accesses the database. Retain worker result and sanitized readiness facts. Capture diagnostics, delete owned Cluster and verify contracted descendants/PVCs are gone even when retaining the worker Job.
3. **Replay/retry:** recover uncertain creates through GET/UID/ownership/spec checks. Foreign same-name Cluster fails without adoption/deletion. Replay reuses the same attempt database; new attempt provisions fresh database/storage.
4. **Cancel/fail:** cancel during bootstrap/work; durably fence launches, reconcile uncertain operations, stop/confirm worker termination, capture Cluster evidence, then delete companions/anchor and preserve the bootstrap Secret. Missing API/operator, failed storage admission and readiness timeout cannot wait forever. Restart resumes the original deadline and partial cleanup. Test delayed create and unsuspend responses racing cancel/restart: no recreation by stale reconciler, raced worker stopped before database deletion, and no premature Cleaned result.
5. **Cleanup:** await Cluster/descendant deletion. Stuck finalizer, retained storage or missing permission produces visible CleanupPending and durable retries. No forced finalizer removal or deletion of unverified descendants. Prove this contract in a disposable cluster before accepting deployment.

## Acceptance boundary

Both exercises use the same generic composer/reconciler without provider-specific lifecycle branches beyond legacy compatibility. A newly allowlisted custom resource requires a bundle and tested readiness/cleanup contract, not another Formicae schema. Unknown resources are not automatically permitted. Acceptance also covers schema-1 Default/history compatibility and snapshot-conflict checks for the complete extension payload.
