# Shared environment foundation contracts

Planning only. The amended #19 plan is independently approved. All application contracts below use namespace `hhnl.Formicae.Application.Workflows` and existing web camelCase JSON. Actual runtime settings are deliberately not part of this release's audit.

## Configuration and catalog models

```csharp
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnvironmentRuntimeSettings(int? TimeoutLimitSeconds = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnvironmentConfiguration
{
    public int SchemaVersion { get; init; } = 1;
    public EnvironmentRuntimeSettings? Runtime { get; init; }
    public JsonElement? Image { get; init; }
    public IReadOnlyList<JsonElement> Tools { get; init; } = [];
    public IReadOnlyList<JsonElement> McpServers { get; init; } = [];
}

public sealed record EnvironmentSnapshot(string Id, int Revision, string Name,
    string Description, EnvironmentConfiguration Configuration);
public sealed record EnvironmentResponse(string Id, int Revision, string Name,
    string Description, EnvironmentConfiguration Configuration, bool BuiltIn,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record CreateEnvironmentRequest(string Name, string? Description = null,
    EnvironmentConfiguration? Configuration = null);
public sealed record UpdateEnvironmentRequest(int ExpectedRevision, string Name,
    string? Description = null, EnvironmentConfiguration? Configuration = null);
```

Null request Configuration means empty overrides. Normalize request defaults before validation. Configuration in a stored runtime snapshot is mandatory; an explicit missing/null snapshot configuration cannot silently inherit. Runtime omitted/null/empty means no cap. Omitted Tools/McpServers use empty lists; explicit null lists are invalid. Image omitted/JSON null means no override; every non-null value is unsupported. Nonempty Tools/McpServers are unsupported. Reject unknown configuration/runtime properties during deserialization instead of silently dropping settings. Do not apply unmapped-member rejection globally to legacy workflow documents.

Use `ExecutionEnvironmentProfile` as the persistence record name, avoiding collision with System.Environment. Init-only properties: required Id/Name, Description default empty, ConfigurationJson default serialized empty EnvironmentConfiguration, Revision default 1, IsDeleted, CreatedAt and UpdatedAt. Map the new `execution_environments` table. No per-execution environment table or TaskRun column is required.

`EnvironmentService(IEnvironmentStore store, IClock? clock = null)` provides `DefaultEnvironmentId = "default"` and immutable static DefaultSnapshot (revision1, name `Default environment`, empty description/configuration). The default is virtual; Get/List prefer it and never expose a shadowing store row. Create generates GUID N IDs. No mutable built-in row is seeded.

```csharp
public interface IEnvironmentStore
{
    Task<ExecutionEnvironmentProfile?> GetAsync(string id, CancellationToken token);
    Task<IReadOnlyList<ExecutionEnvironmentProfile>> ListAsync(CancellationToken token);
    Task<ExecutionEnvironmentProfile> CreateAsync(ExecutionEnvironmentProfile environment, CancellationToken token);
    Task<bool> TryUpdateAsync(ExecutionEnvironmentProfile replacement, int expectedRevision, CancellationToken token);
}
// EnvironmentService public methods:
Task<EnvironmentResponse?> GetAsync(string id, CancellationToken token);
Task<IReadOnlyList<EnvironmentResponse>> ListAsync(CancellationToken token);
Task<EnvironmentResponse> CreateAsync(CreateEnvironmentRequest request, CancellationToken token);
Task<EnvironmentResponse?> UpdateAsync(string id, UpdateEnvironmentRequest request, CancellationToken token);
Task<bool> DeleteAsync(string id, int expectedRevision, CancellationToken token);
```

Store CAS and soft deletion follow Persona/Custom Task: active rows only, positive expectedRevision, exact revision increment, preserve CreatedAt, stale writes throw EnvironmentConflictException, invalid fields/default mutation throw ArgumentException, unknown ID returns null/false. Store immutable serialized JSON and deserialize owned response values to prevent caller mutation. Normalize name/description, enforce configuration byte limit and supported fields before persistence.

API `/api/environments`, `/api/environments/{id}`: GET WorkflowView; POST/PUT/DELETE ManagementAdmin; POST201/full response, PUT200/full response, DELETE204 with expectedRevision query; 400 validation, 404 absent/deleted, 409 revision conflict using existing `{error}` convention. List default first, then names/IDs deterministically. No new permission model.

## Definition snapshot resolution

Append optional `string? DefaultEnvironmentId = null` and `EnvironmentSnapshot? DefaultEnvironmentSnapshot = null` to WorkflowDefinitionDocument with explicit existing-style JSON property names. New saves resolve one workflow-default reference exactly once, discard supplied snapshots and persist the authoritative snapshot even for default. Legacy absent reference/snapshot resolves to virtual default. Explicit `default` references with no snapshot also preserve default compatibility. A supplied default snapshot must equal the immutable default in identity/revision/configuration; it cannot carry a custom cap.

```csharp
public sealed record EnvironmentDefinitionResolution(
    WorkflowDefinitionDocument Document, WorkflowDefinitionValidationResult Validation);
public static class EnvironmentDefinitions
{
    public static WorkflowDefinitionValidationResult ValidateConfiguration(EnvironmentConfiguration? configuration);
    public static Task<EnvironmentDefinitionResolution> ResolveAsync(
        WorkflowDefinitionDocument document, EnvironmentService? environments, CancellationToken token);
    public static WorkflowDefinitionValidationResult ValidateRuntime(WorkflowDefinitionDocument document);
    public static EnvironmentSnapshot? ResolveForTask(
        WorkflowDefinitionDocument document, WorkflowDefinitionStep step);
}
```

ValidateConfiguration rejects null, unsupported schema/extension keys and bounds. Catalog service validates name/description separately. ResolveAsync returns document-level errors with paths defaultEnvironmentId/defaultEnvironmentSnapshot; a missing custom ID retains its reference with null snapshot for disabled drafts. Runtime never consults the catalog. ValidateRuntime checks custom selected ID matches snapshot ID, revision positive, valid name/description/configuration and default immutability.

ResolveForTask returns validated selected/default snapshot for PersonaDefinitions.IsAiTask(step.Uses), otherwise null. It is the single extension point where #17 later introduces per-step overrides. Existing graph normalization preserves the new document fields through record `with` copies. The definition service combines environment, custom task and persona validation before enabled saves and validates pinned environments when resolving a version for a run.

## Runtime bridge and deadline semantics

Append optional `EnvironmentSnapshot? EnvironmentSnapshot = null` to AgentTask after existing optional fields. Common PrepareAgentTaskAsync resolves it for both ordinary AI tasks and Parallel Plan launches, preserving prompt/model/persona/context/attempt/Custom timeout fields. Platform authentication/model-discovery operations never gain environment selection.

Append optional `int? TimeoutLimitSeconds = null` to RuntimeJobSpec. OpenHandsAgentRunner transfers only the validated selected profile's cap; it does not resolve deployment-default timeouts itself. A shared infrastructure policy helper should compute:

```csharp
RuntimeJobExecutionPolicy Resolve(RuntimeJobSpec spec, int runtimeDefaultTimeoutSeconds)
```

Start from existing explicit ExecutionPolicy or the adapter's runtime default. Without a cap, preserve exactly existing timeout/grace normalization and worker propagation rules. With a cap, validate1–3600, take min(existing timeout, cap), clamp grace to0..effectiveTimeout-1, and force worker timeout/grace environment propagation even when spec.ExecutionPolicy was null. Both adapters consume the same calculation, preventing different cap semantics.

For capped jobs also emit `FORMICAE_ENVIRONMENT_TIMEOUT_LIMIT=true` as an internal worker flag (no catalog-controlled environment-variable map). This distinguishes a newly enforced environment deadline from unchanged no-cap timing. WorkerEnvironment appends an optional bool flag to preserve existing test constructors. RuntimeJobStartResult is unchanged; no job-inspection API is introduced.

The worker uses a hard process-tree deadline for capped non-commit tasks and capped commit tasks with zero effective checkpoint grace. Preserve current positive-grace Codex checkpoint behavior. The OpenHands path currently has no worker checkpoint implementation: a capped OpenHands task therefore uses the hard deadline at effective timeout even if its runtime grace is positive, without introducing automatic commits. For capped Codex Implement/AddressComments with zero grace, retain normal successful commit/push behavior and cancel the whole ordinary execution path at deadline; timeout must not trigger a new checkpoint. Custom keeps its #15 hard deadline and never gains commit behavior. Default/no-cap tasks remain unchanged.

The runtime-enforced timeout and worker timeout use the same effective value. Tests must cover cap1 for Implement/AddressComments, prior-null ExecutionPolicy for Plan, and Custom's smaller explicit timeout. A cap larger than an existing task/runtime timeout never increases it. Positive checkpoint grace cannot exceed the capped timeout.

## Audit, editor state and verification ownership

Extend the existing task settings event with an `environment` object containing only `{ id, revision, name, timeoutLimitSeconds }`. Include the TaskRun ID and, where available, durable ExecutionAttemptId through the existing event context/details. This is pinned profile configuration, not actual job facts; never add recomputed image/provisioning/effective-timeout fields on reattachment. Uncertain retries retain the same pinned profile constraints. Explicit user retry still reads the pinned definition, never current catalog data.

The editor stores DefaultEnvironmentId and server-owned snapshot, excludes the snapshot from dirty/undo comparisons, and derives saved revision previews from savedDraft baseline. Successful delayed saves update baseline without erasing later selection edits. History uses existing events and pinned definition data; no new TaskRunResponse fields are required for this foundation.

Catalog/domain/persistence owner implements models/service/store/API/generated migration and CRUD/CAS/upgrade tests. Definition owner implements shared helpers, version/runtime validation and editor serializer compatibility. Runtime owner implements common preparation, policy cap/worker propagation/deadlines/events and real worker/runtime tests. Frontend owner implements catalog/default selector/saved preview/history and browser regressions. Shared signatures are agreed before implementation; builds remain serialized.
