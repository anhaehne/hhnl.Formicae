# References for Kubernetes capability bundles

## Product and previous work

Read `agent-os/product/features.md` execution environments, orchestration/operations and deployment sections; `mission.md`, `roadmap.md`, `tech-stack.md`; and `agent-os/standards/index.yml`. The baseline revision `kubernetes-capability-bundles` governs this spec. Existing environment extensions: `docs/workflow-environment-extensions.md`, `docs/job-runtimes.md` and `agent-os/specs/2026-10-06-1941-environment-extensions/`.

## Application and persistence

`src/hhnl.Formicae.Application/Workflows/EnvironmentModels.cs`, `EnvironmentDefinitions.cs`, `EnvironmentService.cs`, `WorkflowExecutionExtensions.cs`: strict JSON contracts, save-time snapshots, explicit equality normalization, inherited versus explicit capabilities, reserved aliases and current script restrictions. Extend their existing validation/resolution flow rather than resolving bundles at launch from the catalog.

`src/hhnl.Formicae.Infrastructure/Persistence/EfEnvironmentStore.cs` and the existing workflow attempt persistence provide catalog revision and durable-state patterns. `src/hhnl.Formicae.Infrastructure/RuntimeJob.cs` currently carries boolean browser/nested-container requirements; replace the hardcoded infrastructure path with a resolved plan while retaining the legacy adapter.

## Kubernetes, UI and verification

`src/hhnl.Formicae.Infrastructure/Kubernetes/KubernetesJobRunner.cs`: BuildJob already constructs typed native objects and a restartable-init DinD sidecar with startup probe, socket/storage volumes and resources. StartJob currently attaches on deterministic-name conflicts; broaden attachment validation for bundle plans. ReadLogs currently reads the worker only. Cleanup currently depends partly on retained Job settings. `KubernetesJobManifest.cs` is a separate minimal renderer; verify its consumers before introducing a second rendering path.

`src/hhnl.Formicae.Api/ClientApp/src/EnvironmentsPage.tsx`, `EnvironmentExtensions.tsx`, `EnvironmentHistory.tsx`, `workflowEditor/EnvironmentPicker.tsx`: reuse selection, revision/history and inspector conventions. `deploy/helm/formicae/templates/rbac.yaml` and `deploy/kubernetes/base/rbac.yaml`: current allowlist lacks Job patch and general companion/custom-resource permissions; extend only explicitly configured API groups/resources/verbs.

`tests/hhnl.Formicae.Tests/EnvironmentDefinitionTests.cs`, `RuntimeEnvironmentExtensionTests.cs`, `EnvironmentRuntimePolicyTests.cs`, `RuntimeLifecycleTests.cs`; frontend `tests/e2e/stepEnvironments.spec.ts`; Kubernetes `EnvironmentExtensionsE2ETests.cs`, `ExecutionOperationsE2ETests.cs`: existing compatibility/provisioning/lifecycle tests to extend. `.github/workflows/test.yml`, `build-containers.yml`, `deploy-formicae.yml` define release verification.

## Native APIs and external references

[Kubernetes sidecars](https://kubernetes.io/docs/concepts/workloads/pods/sidecar-containers/) document restartable-init lifecycle, startup gating and Job completion. [Job API](https://kubernetes.io/docs/reference/kubernetes-api/batch/job-v1/) and [Job suspension](https://kubernetes.io/docs/concepts/workloads/controllers/job/#suspending-a-job) define suspended startup and deadline behavior. [Owners/dependents](https://kubernetes.io/docs/concepts/overview/working-with-objects/owners-dependents/) define namespace/scope constraints; owner references supplement the application ledger.

The repository already references KubernetesClient **19.0.2**. [GenericClient source](https://github.com/kubernetes-client/csharp/blob/master/src/KubernetesClient/GenericClient.cs) provides a native generic-resource path to investigate in the installed version. [Server-side apply](https://kubernetes.io/docs/reference/using-api/server-side-apply/) is available for field ownership, but cannot replace application composition/identity validation; do not force ownership over foreign objects.

CloudNativePG is a concrete design fixture: [bootstrap](https://cloudnative-pg.io/docs/1.27/bootstrap/), [service management](https://cloudnative-pg.io/docs/1.27/service_management/) and [troubleshooting/readiness](https://cloudnative-pg.io/docs/1.27/troubleshooting/). Its Cluster API demonstrates operator-backed database provisioning; pin the exact test operator version/images and verify readiness/descendant deletion before implementation acceptance. No operator is installed by this design change.
