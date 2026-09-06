# Shared implementation contracts

Planning contract for issue #15. Names below are shared across catalog, definition resolution, orchestration and editor work. All C# application types live in `hhnl.Formicae.Application.Workflows`; JSON uses existing web camelCase serialization. This adds no implementation.

## Catalog and document DTOs

```csharp
public sealed record CustomTaskInputDefinition(
    string Name, string ValueType, bool Required = false, JsonElement? DefaultValue = null);
public sealed record CustomTaskRunnerSettings(string Kind = "agent", int TimeoutSeconds = 1800);
public sealed record CustomTaskSnapshot(
    string Id, int Revision, string Name, string Description, string PromptTemplate,
    IReadOnlyList<CustomTaskInputDefinition> Inputs, CustomTaskRunnerSettings Runner);
public sealed record WorkflowCustomTaskSettings(
    string TaskId, IReadOnlyDictionary<string, JsonElement>? Inputs = null,
    CustomTaskSnapshot? Snapshot = null);
public sealed record CustomTaskResponse(
    string Id, int Revision, string Name, string Description, string PromptTemplate,
    IReadOnlyList<CustomTaskInputDefinition> Inputs, CustomTaskRunnerSettings Runner,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record CreateCustomTaskRequest(
    string Name, string PromptTemplate, string? Description = null,
    IReadOnlyList<CustomTaskInputDefinition>? Inputs = null,
    CustomTaskRunnerSettings? Runner = null);
public sealed record UpdateCustomTaskRequest(
    int ExpectedRevision, string Name, string PromptTemplate, string? Description = null,
    IReadOnlyList<CustomTaskInputDefinition>? Inputs = null,
    CustomTaskRunnerSettings? Runner = null);
```

Append optional `WorkflowCustomTaskSettings? CustomTask = null` to `WorkflowDefinitionStep`. Only `builtins.custom-task` may carry it. Null request Inputs means an empty schema; null request Runner means the default agent runner. Null node Inputs means no supplied values. Snapshot input schemas and Runner are required, and malformed JSON null members must produce validation errors rather than null-reference failures. Input names and scalar type strings are case-sensitive. Number values/defaults must be exactly representable as decimal, have abs(value) <= 9007199254740991, and convert through finite double shortest invariant `R` text back to that same decimal. Reject original tokens that decimal parsing would round or underflow. The editor retains raw numeric text while editing, rejects invalid/precision-losing text visibly, and only commits a round-trip-safe number; no lossless JSON codec or arbitrary-precision claim is introduced. Clone JsonElements retained beyond parsing and use owned dictionaries/lists so catalog snapshots cannot share caller-mutable state.

The persistence record `CustomTaskDefinition` follows Persona's init-only entity convention: required Id/Name/PromptTemplate, Description default empty, InputsJson default `[]`, RunnerJson default serialized default runner, Revision default 1, IsDeleted, CreatedAt and UpdatedAt. Store JSON strings internally; API and snapshots expose typed fields. There is no built-in catalog row or `builtIn` flag.

## Catalog service, store and API

```csharp
public interface ICustomTaskStore
{
    Task<CustomTaskDefinition?> GetAsync(string id, CancellationToken token);
    Task<IReadOnlyList<CustomTaskDefinition>> ListAsync(CancellationToken token);
    Task<CustomTaskDefinition> CreateAsync(CustomTaskDefinition task, CancellationToken token);
    Task<bool> TryUpdateAsync(CustomTaskDefinition replacement, int expectedRevision, CancellationToken token);
}
// Constructor: CustomTaskService(ICustomTaskStore store, IClock? clock = null)
// Public service methods:
Task<CustomTaskResponse?> GetAsync(string id, CancellationToken token);
Task<IReadOnlyList<CustomTaskResponse>> ListAsync(CancellationToken token);
Task<CustomTaskResponse> CreateAsync(CreateCustomTaskRequest request, CancellationToken token);
Task<CustomTaskResponse?> UpdateAsync(string id, UpdateCustomTaskRequest request, CancellationToken token);
Task<bool> DeleteAsync(string id, int expectedRevision, CancellationToken token);
```

Get/List exclude deleted rows. Create generates GUID `N` ID, revision 1 and timestamps. Update/Delete compare positive expectedRevision and atomically replace only active rows at that revision; replacement increments revision exactly once and preserves CreatedAt. Failed CAS throws `CustomTaskConflictException`; invalid requests throw ArgumentException. Unknown/deleted ID yields null/false. Service normalizes name/description, preserves template text, and delegates schema/template/runner validation to the shared definition helper before persistence.

Routes `/api/custom-tasks` and `/api/custom-tasks/{id}` mirror Personas: GET WorkflowView; POST/PUT/DELETE ManagementAdmin. POST 201/full response, PUT 200/full response, DELETE 204 with `expectedRevision` query; unknown 404; invalid 400 `{error}`; conflict 409 `{error}`. List sorted name then ID. No new permission or authentication path.

## Resolution, validation and preparation helpers

Root owns `CustomTaskDefinitions` and its implementation-private tokenizer. Publish these shared entry points:

```csharp
public const string Uses = "builtins.custom-task";
public static WorkflowDefinitionValidationResult ValidateCatalog(
    string? name, string? description, string? promptTemplate,
    IReadOnlyList<CustomTaskInputDefinition>? inputs, CustomTaskRunnerSettings? runner);
public static Task<CustomTaskDefinitionResolution> ResolveAsync(
    WorkflowDefinitionDocument document, CustomTaskService? tasks, CancellationToken token);
public static WorkflowDefinitionValidationResult ValidateRuntime(WorkflowDefinitionDocument document);
public static PreparedCustomTaskExecution Prepare(WorkflowCustomTaskSettings settings, Workflow workflow);
public static void ValidatePrepared(PreparedCustomTaskExecution prepared, WorkflowCustomTaskSettings settings);
public sealed record CustomTaskDefinitionResolution(
    WorkflowDefinitionDocument Document, WorkflowDefinitionValidationResult Validation);
```

Catalog request default normalization happens before ValidateCatalog; runtime snapshots cannot silently acquire missing mandatory fields. ResolveAsync replaces supplied snapshots with authoritative current catalog results, reading each distinct ID once, and returns node-referenced errors. A null service never resolves custom IDs; disabled-draft policy remains the definition service caller's responsibility. ValidateRuntime validates only pinned snapshots, settings and typed inputs without catalog reads. Prepare requires a valid snapshot, applies input defaults, captures referenced allowlisted workflow fields and renders once; invalid preparation throws InvalidOperationException with a user-readable reason. ValidatePrepared checks stored format, identity/revision against the pinned snapshot, typed resolved inputs and bounds; it does not recapture workflow values or rerender from live state. The tokenizer need not be a public API.

## Durable execution and runtime bridge

```csharp
public sealed record PreparedCustomTaskExecution(
    string TaskId, int Revision, string Name,
    IReadOnlyDictionary<string, JsonElement> Inputs,
    IReadOnlyDictionary<string, JsonElement> WorkflowFields,
    int TimeoutSeconds, string Prompt, int FormatVersion = 1);
```

Inputs contains resolved present values only; absent optional values remain absent. WorkflowFields contains only referenced fields keyed by the allowlisted field name (`issueUrl`, `planArtifact`, etc.); missing nullable workflow fields are JSON null. Prompt is rendered before persona composition. FormatVersion makes durable payload interpretation explicit. Prepare and ValidatePrepared enforce the approved limits; the orchestrator additionally checks final composed prompt is nonblank and within its byte limit.

Append nullable `string? CustomTaskExecutionJson` to TaskRun. Serialize PreparedCustomTaskExecution using the established web options, persist it together with the first durable ExecutionAttemptId before calling the agent, and preserve its exact content on explicit retry and uncertain launch. A new loop iteration owns a separate payload. A nonblank malformed/unsupported payload fails clearly; never replace it silently with live values. Catalog/persona snapshots remain in the pinned workflow version, avoiding redundant full copies on every run.

Append optional `int? TimeoutSeconds = null` to AgentTask after ExecutionAttemptId, preserving existing call sites. Only the Custom runner path supplies it; other kinds retain their current policy. Custom requires a valid 1–3600 timeout and maps to `RuntimeJobExecutionPolicy(timeout, 0)` plus the independent worker process-tree deadline. It uses the single fixed `custom-task-inputs.json` context file containing the prepared Inputs and WorkflowFields objects; prompt preparation must not depend on reading that file later. Shared persona preparation retains timeout, context files, model selection and attempt identity.

Append enum members Custom to TaskRunKind/WorkflowStep and Running to WorkflowStatus, preserving all ordinals. Custom dispatch uses its exact definition node and iteration. Generic output is exclusively assigned by result polling; worker Custom messages become bounded task-linked logs, including late terminal callbacks, without updating TaskRun.Output or Status.

Task history adds optional typed `PreparedCustomTaskExecution? CustomTaskExecution` alongside existing Output. Legacy null metadata yields null. Deserialize defensively for historical corrupted/unsupported metadata, preserving the rest of history and showing a clear unavailable-metadata state. No new history endpoint is needed.

## Ownership and coordination

- Catalog/domain/persistence agent: records, WorkflowModels additions, CustomTaskService, stores/EF/DI/catalog routes, generated migration and catalog/API/PostgreSQL tests.
- Root: shared template/resolver/helpers, workflow version/runtime validation integration, graph/type mapping, retry/history service/API wiring as coordinated with runtime agent.
- Runtime agent: generic orchestrator path, durable prepared payload consumption, runner timeout/worker scratch handling, exact callback correlation and execution/runtime tests.
- Frontend agent: API types, catalog/editor/history, node adapter/state snapshot semantics, browser tests.

Compile/test processes remain serialized. Changes to shared signatures are announced before implementation so callers do not develop against divergent types.
