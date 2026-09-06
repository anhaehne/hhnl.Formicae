# Environment planning references

Inspected 2026-09-06; paths and findings describe the pre-implementation code. No implementation changes were made while recording this plan.

## Issue requirements

- [#19 Customizable environments](https://github.com/anhaehne/hhnl.Formicae/issues/19): reusable configuration, validation, selection and preserved defaults.
- [#17 Per-step environments](https://github.com/anhaehne/hhnl.Formicae/issues/17): individual selection, runtime resolution and persisted history.
- [#21 Custom Docker images](https://github.com/anhaehne/hhnl.Formicae/issues/21): image and pull configuration, default compatibility, preflight validation.
- [#22 Tool installs](https://github.com/anhaehne/hhnl.Formicae/issues/22): bootstrap installs, captured output and clear failures.
- [#18 Per-step secrets](https://github.com/anhaehne/hhnl.Formicae/issues/18): selected-only injection, reference validation and redacted output.
- [#20 MCP integration](https://github.com/anhaehne/hhnl.Formicae/issues/20): environment MCP definitions, injection and scoped credentials.
- [#16 Per-step capabilities](https://github.com/anhaehne/hhnl.Formicae/issues/16): restricted available tools/integrations, enforcement and visible audit configuration.

Issue bodies were read with `gh issue list --state open --limit 100 --json number,title,body`.

## Existing implementation

- `src/hhnl.Formicae.Application/Workflows/WorkflowModels.cs`: task-node definitions, `AgentTask`, task response and AI authentication models. Extend optional selection/configuration without losing normalized legacy behavior.
- `src/hhnl.Formicae.Infrastructure/OpenHands/OpenHandsAgentRunner.cs`: `StartAsync` resolves AI configuration and repository token; `BuildSpec` supplies global image, task-dependent execution requirements and secret material. Main integration point for effective environments.
- `src/hhnl.Formicae.Infrastructure/RuntimeJob.cs`: existing runtime-neutral image, command, context files, secret files/environment, execution requirements and deadline policy. Extend this path instead of introducing a second launcher.
- `src/hhnl.Formicae.Infrastructure/Kubernetes/KubernetesJobRunner.cs`: production typed `V1Job` construction, scoped Secrets, context ConfigMap, task-dependent DinD sidecar and cleanup. Worker image uses `IfNotPresent`; service-account automount is disabled. Runtime restrictions must be applied here.
- `src/hhnl.Formicae.Infrastructure/Kubernetes/KubernetesJobManifest.cs`: simple text renderer; not the complete production construction path.
- `src/hhnl.Formicae.Infrastructure/Containers/ContainerJobRuntime.cs`: local runtime passes secret environment values as process arguments and writes mounted secret files beneath the job workspace. Preserve supported local execution while improving secret transport/cleanup.
- `src/hhnl.Formicae.Worker/Program.cs`: worker setup, checkout, CLI initial/resume arguments, process reporting and `CodexWorkspace.Prepare`. Initial and resumed Codex launches currently bypass approvals/sandbox. Redaction currently accepts a single git token. Workspace preparation copies auth-mount `config.toml` and appends Playwright MCP when required.
- `src/hhnl.Formicae.Infrastructure/OpenHands/ModelDiscoveryService.cs`: CLI model-discovery jobs already use runtime/auth helpers. Preserve CLI discovery and do not inadvertently apply arbitrary task environments to authentication/discovery jobs.
- `src/hhnl.Formicae.Infrastructure/Persistence/FormicaeDbContext.cs`: existing configuration persistence patterns. Generate proper migrations for new persistent entities.
- `src/hhnl.Formicae.Api/Program.cs`: management authorization and API conventions; protect configuration mutation and omit secret values from responses.
- `agent-os/product/tech-stack.md`: React/TypeScript/Vite, ASP.NET Core, PostgreSQL and ephemeral Kubernetes workloads remain product defaults.

## Supported CLI configuration research

Context7 resolved official `/openai/codex`, then queried MCP and execution configuration. Relevant upstream sources:

- [MCP schema](https://github.com/openai/codex/blob/main/codex-rs/config/src/mcp_types.rs): stdio command/args/cwd/env_vars; HTTP url/bearer_token_env_var/env_http_headers; enabled_tools and disabled_tools.
- [MCP configuration tests](https://github.com/openai/codex/blob/main/config/src/mcp_types_tests.rs): name-only `env_vars` inherited at runtime.
- [Shell environment policy](https://github.com/openai/codex/blob/main/codex-rs/protocol/src/config_types.rs): configurable shell inheritance/filtering. Environment filtering alone is not an isolation guarantee when arbitrary shell access can inspect local credentials.
- [Network proxy documentation](https://github.com/openai/codex/blob/main/codex-rs/network-proxy/README.md): supported policy mechanisms exist upstream, but require version/platform/runtime verification before this application can claim enforcement.

The worker invokes an unpinned npm CLI. Upstream main documentation is not proof that a deployed CLI supports a given field; verify the installed version and actual behavior during implementation.

## Risks and boundaries

1. MCP allowlists hide tools but do not constrain arbitrary shell/network execution under sandbox bypass.
2. Copying user/project configuration may widen intended capabilities; restricted execution needs authoritative composition and override handling.
3. Custom images/bootstrap commands are trusted executable management configuration, not safe because an image reference parses.
4. Scoped secrets need central redaction before bootstrap or MCP can safely emit output. Distinguish required platform authentication from optional step secrets.
5. Codex and OpenHands have different integration surfaces; unsupported combinations must fail clearly.
6. Revisions/snapshots are necessary to make retries and historical environment selections reproducible without persisting secret values.
7. Live credential provisioning, registry access or cluster policy changes need separate target-specific authorization; do not silently introduce workaround infrastructure.
