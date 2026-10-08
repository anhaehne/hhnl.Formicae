# Agent task integration contract

Status: agreed with the Agent task implementer on 2026-10-08. Detailed reply retained below; proposed image feature remains unimplemented.

## Node fields

Extend WorkflowDefinitionStep and frontend graph/API types with optional imageSelection and server-resolved imageSnapshot. Existing definitions omit both and retain current behavior.

```json
{
  "uses": "builtins.agent-task",
  "imageSelection": { "mode": "managed", "imageId": "img-123", "buildId": "build-456" },
  "imageSnapshot": {
    "imageId": "img-123", "buildId": "build-456", "sourceRevision": 2,
    "name": "Dotnet tools", "reference": "registry.example/formicae/local/img-123@sha256:<64-hex-digest>",
    "pullPolicy": "IfNotPresent", "pullSecretNames": ["formicae-images-pull"],
    "workerProtocolVersion": 1, "platform": "linux/amd64"
  }
}
```

Names are proposed. The other branch uses customTask.definition for inline prompts/schemas. Image selection belongs on the step beside environmentId/environmentSnapshot, outside the inline definition.

## Precedence

| Selection | Effective image | Other environment settings |
| --- | --- | --- |
| Omitted / inherit | Existing step/workflow environment image, then platform default | Existing behavior |
| platform | Platform worker and platform pull configuration | Retained |
| managed | Exact snapshot digest and managed pull configuration | Retained |

No implicit latest-build mode. BuildId identifies the chosen immutable artifact. imageSnapshot exists only for managed selection; reject other combinations. Platform mode ignores environment image pull policy/secrets and uses platform settings.

## Save and runtime

At Save Version validate catalog ownership, active definition, Ready state, digest, architecture and compatibility. Disabled drafts may retain incomplete selections with validation diagnostics following existing behavior; enabled versions require complete snapshots. Copy trusted values server-side and reject forged snapshots. Archives cannot be newly selected but saved versions retain artifacts.

Resolve effective image once from saved step/environment and carry it into AgentTask/attempt audit/RuntimeJobSpec. No latest-build substitution or catalog lookup during scheduling/resume/retry. Preserve saved environment identity. Registry credential values stay outside workflow snapshots; only operator-managed reference names are recorded.

## Ownership and coordination

Other agent owns builtins.agent-task authoring/prefilling and existing environment propagation. This feature owns catalog/builds/registry/future image fields/resolver/picker. Do not ask the other agent to implement image building.

Initial request sent through Paseo on 2026-10-08 around 17:07 UTC. Implementer read this contract, agreed the names/precedence/ownership, and reported no conflicting requirements.

## Confirmed integration hooks

`CustomTaskDefinitions.AgentUses` is `builtins.agent-task`, mapped to TaskRunKind.Custom. Inline settings remain customTask.definition. Workflow-level defaults are defaultEnvironmentId/defaultEnvironmentSnapshot; per-step fields are environmentId/environmentSnapshot.

Save hook: WorkflowDefinitionService.CreateVersionAsync calls EnvironmentDefinitions.ResolveAsync after persona/task resolution. Runtime validates through EnvironmentDefinitions.ValidateRuntime and selects the saved environment through ResolveForTask. WorkflowOrchestrator.Custom.cs calls PrepareAgentTaskAsync, carrying EnvironmentSnapshot/capabilities/secrets; OpenHandsAgentRunner propagates image reference/policy/secrets to RuntimeJobSpec and KubernetesJobRunner.

The implementer prefers an effective execution copy of environment configuration with only Image changed. Keep the original saved environment immutable and record image/build provenance separately in attempt audit. This can reuse existing launcher propagation without adding a second conflicting image-resolution path. Platform mode must explicitly use platform pull configuration.

Frontend integration files: workflowEditor/Inspector.tsx, AgentTaskSettings.tsx, EnvironmentPicker.tsx, api.ts and workflowGraph.ts. New image selection belongs beside environment settings rather than inside AgentTaskSettings prompt/schema data.

Implementer-reported verification: Inline_agent_task_executes_with_persona_and_declared_outputs passes with a digest image, Always policy, pull-secret reference, tools/MCP/timeout retained; targeted runtime/persona suite passed 33 tests. These were run on sulky-fox, not rerun by this planning branch. Agent Uses participates in PersonaDefinitions.IsAiTask and WorkflowExecutionExtensions.IsExecutionTask; its runtime guard now compares mapped task kinds. Browser/deployment validation continues independently.
