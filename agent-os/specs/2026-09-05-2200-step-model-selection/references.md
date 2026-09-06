# References

- WorkflowModels, WorkflowOrchestrator, AiSettingsService: definitions, execution and profile resolution.
- OpenHandsAgentRunner, CodexAuthSetupService, WorkerCommand: runtime and credential handling.
- WorkflowDefinitionsPage and workflowGraph: selected-step editor and serialization.
- https://learn.chatgpt.com/docs/app-server : initialize and paginated model/list over CLI stdio.
- Official Codex SDK documentation was checked. This .NET worker uses its existing process infrastructure and System.Text.Json for the bounded request/response exchange rather than introducing a second-language SDK host.
