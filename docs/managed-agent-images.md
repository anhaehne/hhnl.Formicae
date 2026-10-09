# Managed agent images (0.24.0)

Manage → Images prepares custom execution images before users select them in Agent tasks or environment profiles. Save an inline/uploaded Dockerfile (empty build context), or select an already connected repository, branch, Dockerfile path and context path. Repository builds resolve the branch to a commit before queuing. GitHub and Gitea use the existing repository adapters; Azure DevOps requires its repository adapter to be implemented. Inline Dockerfiles cannot COPY local files. Build arguments are supported through the API for non-secret values; credential uploads and archive contexts are outside this release.

Start with the matching Formicae worker image, preferably pinned by digest:

```dockerfile
FROM docker.io/limeray/hhnl-formicae-worker:0.24.0
RUN apt-get update && apt-get install -y --no-install-recommends jq \
    && rm -rf /var/lib/apt/lists/*
```

Keep the worker application, working directory, .NET runtime, Git, shell, Node and Python intact. Initial builds target linux/amd64. A build becomes Ready only after publication, registry digest verification and a credential-free `dotnet hhnl.Formicae.Worker.dll --check-runtime` Job succeeds. This structural probe does not call an AI model or prove every custom tool works; validate task-specific tools in your own Dockerfile/tests. Development adapters simulate builds and are explicitly labelled in the UI.

The lifecycle is Queued → Building → Validating → Ready, with Failed, Cancelled and TimedOut terminal outcomes. Build logs are bounded and infrastructure failures do not expose credential details. Retry creates a new build attempt. Editing source creates an immutable source revision. Failed rebuilds retain previous successful artifacts. Administrator access is required to edit, build, cancel or archive; viewers can inspect the catalog.

In the workflow inspector, Execution image supports inheriting the environment image, using the platform image, or selecting a prepared image and exact Ready build. Save Version resolves a trusted digest and freezes provenance and pull settings. Rebuilds do not retarget saved versions or retries. Image overrides retain environment tools, MCP servers, timeouts and other settings. Archived sources cannot be newly selected; saved versions remain runnable with their recorded image. Private managed images initially require Kubernetes; the container runtime rejects Kubernetes pull-secret settings rather than dropping authentication.

## Operator setup

Managed builds are opt-in (`managedImages.enabled: false` by default). The chart can deploy CNCF Distribution with PVC-backed storage and native token authentication, or use an external registry. These components use the maintained Kubernetes client, BuildKit and Microsoft IdentityModel JWT implementation.

Choose a canonical HTTPS registry hostname reachable and resolvable by **Kubernetes nodes and builder pods**, and an HTTPS API token endpoint reachable by both. A ClusterIP-only service name is insufficient for node image pulls. Supply routing (LoadBalancer or your ingress), matching certificates and node/container-runtime CA trust. Builders must trust the registry certificate too; use publicly trusted certificates or a builder image with your CA installed. API registry requests use the API container trust store. The chart does not configure host DNS or node CA trust.

Create these operator-managed Secrets before enabling builds:

- Registry namespace: `tlsSecretName`, a TLS Secret with `tls.crt`/`tls.key` for the canonical registry hostname.
- API/registry namespace: `tokenSigningSecretName`, with PEM `cert.crt` and unencrypted RSA `key.key`. The registry receives only the certificate; the API receives the signing key.
- API namespace: `credentialsSecretName`, with `pull-password` (at least 24 random characters).
- Worker **and build** namespaces: the `pullSecretNames` dockerconfigjson Secrets containing `agent:<pull-password>` for the exact registry hostname/port. These credentials authorize pull only under the configured repository prefix.

Example values (replace endpoints and Secret names):

```yaml
managedImages:
  enabled: true
  registry: images.example.com
  repositoryPrefix: formicae/installation-a
  workerBaseImage: docker.io/limeray/hhnl-formicae-worker:0.24.0
  buildNamespace: formicae-image-builds
  nodeSelector:
    formicae.io/image-builder: "true"
  pullSecretNames: [formicae-image-pull]
  concurrency: 2
  timeoutSeconds: 1200
  registryDeployment:
    enabled: true
    serviceType: LoadBalancer
    tlsSecretName: formicae-registry-tls
    tokenSigningSecretName: formicae-registry-signing
    credentialsSecretName: formicae-registry-credentials
    tokenRealm: https://formicae.example.com/api/registry/token
    storage: 20Gi
```

The image-builder service account has no mounted Kubernetes API token. The API receives scoped RBAC for Jobs, ConfigMaps and Secrets in the build namespace. Each attempt has isolated emptyDir context/cache volumes, limits, deadline and deterministic resources for restart reconciliation. Git credentials exist only in the preparation init container; no AI credentials, workspace PVC or host Docker socket enter a build. Short-lived build capabilities allow base-image pulls within the installation prefix and restrict publication to that build's repository; registry pull passwords cannot push.

Rootless BuildKit requires compatible Linux user namespaces and unconfined seccomp/AppArmor. The dedicated build namespace therefore has an explicit privileged Pod Security admission label, although builder containers do not run privileged or mount host devices. BuildKit's no-process-sandbox mode reduces process isolation inside a build; treat Dockerfiles as trusted administrator code and use a dedicated build node pool. Validate your actual node/kernel policy before enabling the feature. The chart exposes an optional NetworkPolicy with operator-defined egress for DNS, registry, source control and package mirrors; default network policy is unchanged. Do not enable deny-all egress without those rules.

For an external registry, set `registryDeployment.enabled: false`, supply `pushSecretName` (dockerconfigjson in the build namespace), and supply pull-only Secrets in worker/build namespaces. The API reads the push Secret to verify published digests. If its Bearer authentication endpoint has a different host, explicitly allow that HTTPS authority through `externalTokenHosts`; cross-host redirects are disabled. External mode renders no bundled registry or token-signing mounts. Scope push credentials to the installation repository prefix.

## Operation and upgrades

Version 0.24.0 startup applies the generated `AddManagedAgentImages` EF migration. Deploy matching API and worker versions. Old custom images may need rebuilding after worker protocol upgrades; an incompatible image fails explicitly without platform-image fallback.

Back up PostgreSQL metadata, registry storage and operator-managed keys/Secrets together. Restore metadata and blobs together so pinned digests remain available. Registry manifest deletion is disabled for the bundled service. Archiving does not delete artifacts, and automatic garbage collection is not implemented: do not remove blobs referenced by saved versions or execution history. Monitor PVC capacity and provision more storage before it fills. Publication, authentication, node trust and storage failures leave a failed build with logs while preserving older Ready builds.

Rotate pull credentials by updating the API password Secret and pull Secrets in both namespaces together, then restart the API. Rotate signing keys by staging trust for the new certificate in the registry before switching API signing material; account for outstanding short-lived tokens. Keep Secret names stable because saved versions freeze pull-secret names. Builds time out after the configured deadline and reconciliation retries cleanup after cancellation or API restarts.

Production rollout requires an actual Dockerfile build/push/probe and an authenticated worker pull from a fresh node on the intended pool. The disposable Kubernetes E2E registry uses HTTP only within its test cluster; production defaults require HTTPS. There is no automatic deployment from this feature branch.
