# Managed agent images — scope

The user requests a plan for Dockerfile-built custom images prepared in Manage Workspace, selectable by Agent tasks, and coordination with the Agent task implementer.

## Implemented decisions

1. Separate Images catalog; environments can also reference prepared images.
2. Isolated asynchronous Kubernetes BuildKit builds; bundled private registry with an external registry option.
3. Exact successful compatible builds with digest snapshots pinned in workflow versions.
4. Node image overrides affect only image settings and inherit existing environment behavior when unset.
5. MVP: worker-compatible linux/amd64, inline Dockerfiles or connected-repository contexts; archive contexts/multi-architecture/build-secret UI follow later. Dockerfile file loading is supported.

## Context

Existing EnvironmentConfiguration.Image supports worker references, pull policy and operator pull secrets. Environment snapshots are already immutable per saved workflow version. Missing components are source catalog/build orchestration/managed storage and direct prepared-image selection.

The management navigation has a Manage group. Existing catalog scope is installation-local; do not infer new tenancy from workspace wording. This feature extends self-hosted environment customization without changing the product mission.

Agent task implementation: sulky-fox, agent e3d3787a-6944-4282-9d32-b2b10511ccb6, builtins.agent-task using inline definition and existing environment/persona runtime. Coordination completed through the Paseo skill; contract names, precedence and ownership agreed, with confirmed hooks and implementer-reported image propagation tests recorded in contracts.md.

No visuals supplied. Reference existing catalog/editor/history UI. The only indexed standard is database/ef-migrations, copied in standards.md.

## Engineering gates

Validate BuildKit against cluster security/kernels, registry endpoint/node TLS trust/storage, and maintained registry integrations. Merge Agent task changes before editing shared contracts. Implementation was authorized by the subsequent “Go ahed”/“Continue” requests. Defaults and verification results are recorded in plan.md.
