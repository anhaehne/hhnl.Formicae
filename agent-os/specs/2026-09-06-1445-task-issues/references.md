# Planning references

Issue requirements read using `gh issue list --state open --limit 30 --json number,title,body` on 2026-09-06:

- https://github.com/anhaehne/hhnl.Formicae/issues/11 — deterministic decisions, persisted selected branches, invalid/ambiguous definition rejection.
- https://github.com/anhaehne/hhnl.Formicae/issues/12 — isolated scripts, output/exit/failure capture, deterministic tests.
- https://github.com/anhaehne/hhnl.Formicae/issues/14 — editable reusable persona configuration, workflow/task attachment, default compatibility, rendered context.
- https://github.com/anhaehne/hhnl.Formicae/issues/15 — reusable task definitions, validated inputs, persisted outputs.
- https://github.com/anhaehne/hhnl.Formicae/issues/10 — parallel execution dependency for branch scheduling.
- https://github.com/anhaehne/hhnl.Formicae/issues/16 — enforced per-step capabilities.
- https://github.com/anhaehne/hhnl.Formicae/issues/17 — per-step environment selection and history.
- https://github.com/anhaehne/hhnl.Formicae/issues/18 — scoped secret references and redacted output.
- https://github.com/anhaehne/hhnl.Formicae/issues/19 — reusable environment definitions and default execution compatibility.

Repository sources inspected (relative to repository root):

- `src/hhnl.Formicae.Application/Workflows/WorkflowModels.cs`: WorkflowStep/TaskRunKind contain four built-in kinds; definition/step JSON records; TaskRun stores DefinitionStepId and LoopIteration; AgentTask and AgentRunResult contracts.
- `src/hhnl.Formicae.Application/Workflows/WorkflowNodeDefinitions.cs`: Validate and Normalize compile v1alpha3 trigger/loop nodes into v1alpha2 sequential tasks and loop lists. Loop bodies currently require sequential tasks.
- `src/hhnl.Formicae.Application/Workflows/WorkflowDefinitionValidator.cs`: task-type mappings, sequential cycle detection, exactly-one-terminal validation.
- `src/hhnl.Formicae.Application/Workflows/WorkflowOrchestrator.cs`: RunPlanningAsync, RunImplementationAsync, CreatePullRequestAsync, AddressPullRequestCommentsAsync have distinct side effects; StartAgentTaskAsync centralizes agent starts; ResolveExecutionContextAsync, GetCurrentTaskRunAsync, and AdvanceDefinitionCursorAsync use a single definition cursor and loop iteration.
- `src/hhnl.Formicae.Infrastructure/Prompts/FilePromptRenderer.cs`: fixed built-in template selection and allowlisted context replacements.
- `src/hhnl.Formicae.Infrastructure/OpenHands/OpenHandsAgentRunner.cs`: StartAsync always resolves AI settings; BuildSpec selects runtime image, credentials, requirements and policy; repository token creation is restricted to selected built-in kinds.
- `src/hhnl.Formicae.Infrastructure/RuntimeJob.cs`: RuntimeJobSpec already supports command, environment, context files, secret files/environment, execution requirements, and timeout policy.
- `src/hhnl.Formicae.Worker/Program.cs`: WorkerEnvironment requires task prompt; checkout and commit permissions are kind-based; WorkerCommand.RunAsync dispatches model discovery/auth setup then AI execution; process runner and timeout helpers exist.
- `src/hhnl.Formicae.Api/ClientApp/src/workflowEditor/catalog.ts`: six existing task/control node catalog entries.
- `src/hhnl.Formicae.Api/ClientApp/src/workflowGraph.ts`: supported type list, legacy conversion, named handles, graph serialization and editor position metadata.

Implementation must re-read current files after predecessor issues land. These findings describe the inspected 0.11.x baseline, not future scheduler changes.
