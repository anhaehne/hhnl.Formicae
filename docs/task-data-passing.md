# Named custom-task outputs (0.18.0)

Custom tasks can declare up to 32 named outputs with a `string`, `number`, or `boolean` type and a required flag. A task with outputs must finish with one strict JSON object. Markdown, duplicate or unknown names, missing required values, nulls, wrong scalar types and oversized values fail the task before the next step starts. Tasks without output schemas keep free-text completion and history.

## Two-task example

Create a producer with prompt `Return a summary containing ready.` and output schema:

```json
[{"name":"summary","valueType":"string","required":true}]
```

The producer's final response is:

```json
{"summary":"ready"}
```

Create a consumer with required string input `summary` and prompt `Use {{input.summary}}`. Connect the producer's control **Next** to the consumer. In the consumer inspector, choose `Producer · summary` as the input source, or connect the named output and input handles. The saved settings are:

```json
{"taskId":"consumer-task-id","bindings":{"summary":{"stepId":"producer","outputName":"summary"}}}
```

The consumer executes with `Use ready`. Task history shows validated structured outputs, frozen resolved inputs and producer run/attempt provenance. Data edges are distinct from control edges and never schedule execution or affect layout. A producer output can feed several consumers, but each input accepts one source and cannot also carry a literal.

## Execution rules

A producer must be guaranteed to execute before the consumer on every control path that can reach it. Self, downstream, conditionally skipped, missing and differently typed sources are rejected. Existing parallel custom-task restrictions remain in place.

Within a loop, outputs resolve from the same iteration. A producer before loop entry can feed its body. Bindings leaving a loop body or crossing loops are rejected. Optional outputs may be omitted; the consumer then uses its declared default or required/optional rule. A configured binding never falls back to a configured literal.

Preparation resolves only successful persisted producer runs with validated outputs and records values and run/attempt identities before launching the agent. Restart and consumer retry reuse this preparation even if source data changes. Producer retry clears its structured outputs. Streaming callbacks remain logs and cannot replace authoritative completion.

Values reuse input limits: strings up to 16,000 characters, wire-safe numbers within ±9,007,199,254,740,991 and at most 28 decimal places, and booleans. Structured responses and resolved input objects are limited to 65,536 UTF-8 bytes. Schema names are case-sensitive identifiers starting with a letter, up to 64 characters.

GitHub Issue created also produces `issue` (the full issue JSON as a string) and `issueId` (the repository-local issue number). Bind event outputs only where that event is the guaranteed selected entrypoint. The full persisted event snapshot is retained; normal input size limits apply when a consumer uses it. Add issue comment accepts `issueId` and `text` from literals or compatible producer outputs and records its frozen inputs and provenance. See [issue event outputs and comments](workflow-start-nodes.md#github-issue-outputs-0250).

## Agent and custom task output correction (0.26.0)

Inline Agent nodes and reusable Custom tasks automatically include their pinned output schema, required/optional rules, scalar bounds and JSON size limit in the effective prompt. Agents do not need hand-written output-format instructions in the task template.

The worker extracts the authoritative final response from each successful CLI turn and applies the same strict output validation as orchestration. Missing final output or invalid JSON/schema values cause a correction message to the same saved Codex or OpenHands conversation, including the validation error and required format. The agent is asked to return corrected output using its completed work. There are at most two correction turns, all within the original task/environment timeout. Failed CLI execution and cancellation do not trigger correction. A CLI without a resumable conversation identity fails explicitly.

Correction messages, numbered progress and agent responses remain in the task attempt's live and retained evidence. Orchestration restart polls the existing worker and does not reset its correction count. Kubernetes does not restart failed worker processes; retry creates a new attempt under the existing retry rules. Downstream tasks start only after corrected output validates and is persisted. Exhaustion fails with the output-validation reason and no structured outputs. Tasks without declared outputs retain free-text completion.

Deploy matching 0.26.1 API and worker images and Helm chart. Rebuild custom images from the matching worker to support correction; no database migration is added. Previously prepared prompts remain readable and gain the full output contract when launched.
