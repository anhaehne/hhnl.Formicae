# Agent tasks

Version 0.22.0 adds the Agent task workflow node (`builtins.agent-task`). Configure its prompt template, typed input and output schemas, timeout, persona, model and environment in the inspector. Inputs support literal values, defaults and upstream output bindings. The agent runs in a scratch workspace using the existing custom-agent runtime.

Use **Prefill from custom task** to copy a catalog template into the node. Prefilling replaces the prompt, schemas and timeout and clears existing input values and bindings; persona selection stays with the node. Edit the copied values freely. Future changes or deletion of the catalog template do not affect the node. Saved workflow versions pin both the inline task definition and the selected persona.

Existing Custom task nodes remain compatible and retain their catalog revision behavior. Prefilling other ordinary task types is deferred. No database migration is required. Deploy matching 0.27.0 API and worker images and Helm chart for output correction.

Agent and Custom tasks with declared outputs receive their output format and bounds directly in the prompt. Missing or invalid final output causes up to two correction messages to the same agent conversation within the original timeout. Only validated outputs reach downstream tasks; correction progress and responses remain in attempt evidence. See [task output correction](task-data-passing.md#agent-and-custom-task-output-correction-0260).
