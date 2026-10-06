# References

GitHub issue requirements: [#12](https://github.com/anhaehne/hhnl.Formicae/issues/12), [#16](https://github.com/anhaehne/hhnl.Formicae/issues/16), [#18](https://github.com/anhaehne/hhnl.Formicae/issues/18), [#20](https://github.com/anhaehne/hhnl.Formicae/issues/20), [#21](https://github.com/anhaehne/hhnl.Formicae/issues/21), [#22](https://github.com/anhaehne/hhnl.Formicae/issues/22).

Existing code: EnvironmentModels/Definitions/Service; WorkflowModels/NodeDefinitions/Validator; WorkflowOrchestrator custom/control/parallel partials; OpenHandsAgentRunner; RuntimeJob; KubernetesJobRunner; ContainerJobRuntime; worker Program.cs/CodexWorkspace/WorkerReporter; EnvironmentsPage and workflowEditor; execution investigator and environment history. Existing JSON snapshots support typed configuration without a new catalog table.

Large-model consultant: environment_extension_design (GPT-6 Astra). Native Codex MCP config and OpenHands MCP adapters are used rather than a generic configuration variable that the harness does not read.

Primary OpenHands references: [CLI MCP](https://docs.openhands.dev/openhands/usage/cli/mcp-servers), [configuration locations](https://github.com/OpenHands/OpenHands-CLI/blob/main/openhands_cli/locations.py), [native MCP utilities](https://github.com/OpenHands/OpenHands-CLI/blob/main/openhands_cli/mcp/mcp_utils.py). Additional primary references and findings are recorded during implementation.

Primary Codex references: [configuration](https://developers.openai.com/codex/config-reference/), [config loader](https://github.com/openai/codex/blob/main/codex-rs/config/src/loader/mod.rs), [merge semantics](https://github.com/openai/codex/blob/main/codex-rs/config/src/loader/merge.rs). The large-model consultant reproduced Codex 0.160.1 CLI configuration: clearing `mcp_servers` and dotted project overrides do not disable project MCP because of recursive merging. Replacing the whole `projects` table with a task working directory marked untrusted disables repository-local MCP on initial execution and resume.

The consultant inspected the packaged OpenHands CLI 1.16.0 / SDK 1.21.0: native `InstalledMCPConfig.from_dict` consumes `mcpServers` with stdio command/args/env or HTTP URL/headers; native AgentStore applies enabled servers. Private `OPENHANDS_PERSISTENCE_DIR` isolates generated configuration. Kubernetes tests check both harness preparation paths through local command probes without calling a model provider.

Image syntax follows the primary [distribution/reference grammar](https://github.com/distribution/reference/blob/main/regexp.go) using existing .NET regular-expression support. The project does not require an additional container client or parser dependency.
