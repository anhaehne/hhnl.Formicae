using System.Text.Json;

namespace hhnl.Formicae.Application.Workflows;

/// <summary>Resolves catalog configuration at save time; execution consumes only the immutable snapshot.</summary>
public static class EnvironmentDefinitions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static WorkflowDefinitionValidationResult ValidateConfiguration(EnvironmentConfiguration? configuration)
    {
        var errors = new List<WorkflowDefinitionValidationError>();
        void Error(string message, string path) => errors.Add(new("environment.configuration.invalid", message, path));
        if (configuration is null)
            return new([new("environment.configuration.required", "Environment configuration is required.", "configuration")]);
        if (configuration.SchemaVersion != 1) Error("Environment configuration schema version must be 1.", "schemaVersion");
        if (configuration.Runtime?.TimeoutLimitSeconds is { } cap && (cap < 1 || cap > 3600))
            Error("Maximum task runtime must be between 1 and 3600 seconds.", "runtime.timeoutLimitSeconds");
        if (configuration.Image is { ValueKind: not JsonValueKind.Null })
            Error("Custom environment images are not supported yet.", "image");
        if (configuration.Tools is null) Error("Environment tools must be an array.", "tools");
        else if (configuration.Tools.Count > 0) Error("Environment tool installation is not supported yet.", "tools");
        if (configuration.McpServers is null) Error("Environment MCP servers must be an array.", "mcpServers");
        else if (configuration.McpServers.Count > 0) Error("Environment MCP configuration is not supported yet.", "mcpServers");
        if (errors.Count == 0 && JsonSerializer.SerializeToUtf8Bytes(configuration, JsonOptions).Length > 32768)
            Error("Environment configuration must not exceed 32768 UTF-8 bytes.", "configuration");
        return new(errors);
    }

    public static async Task<EnvironmentDefinitionResolution> ResolveAsync(
        WorkflowDefinitionDocument document, EnvironmentService? environments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var errors = new List<WorkflowDefinitionValidationError>();
        var cache = new Dictionary<string, EnvironmentSnapshot?>(StringComparer.Ordinal)
        { [EnvironmentService.DefaultEnvironmentId] = EnvironmentService.DefaultSnapshot };
        async Task<EnvironmentSnapshot?> Resolve(string id)
        {
            if (cache.TryGetValue(id, out var cached)) return cached;
            var environment = string.IsNullOrWhiteSpace(id) || environments is null ? null : await environments.GetAsync(id, token);
            var snapshot = environment is null ? null : new EnvironmentSnapshot(environment.Id, environment.Revision,
                environment.Name, environment.Description, environment.Configuration);
            cache[id] = snapshot;
            return snapshot;
        }
        var defaultId = document.DefaultEnvironmentId ?? EnvironmentService.DefaultEnvironmentId;
        var defaultSnapshot = await Resolve(defaultId);
        if (defaultSnapshot is null)
            errors.Add(new("definition.environment.missing", $"Workflow environment '{defaultId}' is unavailable.", "defaultEnvironmentId"));
        var enriched = document with { DefaultEnvironmentSnapshot = defaultSnapshot };
        if (document.Steps is null || document.Steps.Any(step => step is null))
            return new(enriched, new([.. errors, new("definition.steps.required", "Workflow steps must contain non-null entries.", "steps")]));
        var steps = new List<WorkflowDefinitionStep>(document.Steps.Count);
        foreach (var step in document.Steps)
        {
            var resolved = step with { EnvironmentSnapshot = null };
            if (!PersonaDefinitions.IsAiTask(step.Uses))
            {
                if (step.EnvironmentId is not null)
                    errors.Add(new("definition.environment.unsupported", "Only AI tasks can select an environment.", "steps[].environmentId", step.Id));
            }
            else
            {
                var id = step.EnvironmentId ?? defaultId;
                var snapshot = await Resolve(id);
                if (snapshot is null)
                    errors.Add(new("definition.environment.missing", $"Environment '{id}' is unavailable.", "steps[].environmentId", step.Id));
                resolved = resolved with { EnvironmentSnapshot = snapshot };
            }
            steps.Add(resolved);
        }
        enriched = enriched with { Steps = steps };
        return new(enriched, errors.Count == 0 ? ValidateRuntime(enriched) : new(errors));
    }

    public static WorkflowDefinitionValidationResult ValidateRuntime(WorkflowDefinitionDocument document)
    {
        var errors = new List<WorkflowDefinitionValidationError>();
        var defaultId = document.DefaultEnvironmentId ?? EnvironmentService.DefaultEnvironmentId;
        var snapshots = new Dictionary<string, EnvironmentSnapshot>(StringComparer.Ordinal);
        EnvironmentSnapshot? Check(string id, EnvironmentSnapshot? snapshot, string path, string? nodeId = null)
        {
            var validation = ValidateSnapshot(id, snapshot, path, nodeId);
            errors.AddRange(validation.Errors);
            if (!validation.IsValid) return null;
            snapshot ??= EnvironmentService.DefaultSnapshot;
            if (snapshots.TryGetValue(id, out var existing) && !Equivalent(existing, snapshot))
                errors.Add(new("definition.environment.snapshot.conflict", $"Environment '{id}' has conflicting pinned profile configurations.", path, nodeId));
            else snapshots[id] = snapshot;
            return snapshot;
        }
        var defaultSnapshot = Check(defaultId, document.DefaultEnvironmentSnapshot, "defaultEnvironmentSnapshot");
        if (document.Steps is null || document.Steps.Any(step => step is null))
            return new([.. errors, new("definition.steps.required", "Workflow steps must contain non-null entries.", "steps")]);
        foreach (var step in document.Steps)
        {
            if (!PersonaDefinitions.IsAiTask(step.Uses))
            {
                if (step.EnvironmentId is not null || step.EnvironmentSnapshot is not null)
                    errors.Add(new("definition.environment.unsupported", "Only AI tasks may have an environment selection or snapshot.", "steps[].environmentId", step.Id));
                continue;
            }
            var id = step.EnvironmentId ?? defaultId;
            // Metadata-free nodes from 0.16.0 inherit the document's immutable snapshot.
            var snapshot = step.EnvironmentSnapshot ?? (step.EnvironmentId is null ? defaultSnapshot : null);
            Check(id, snapshot, "steps[].environmentSnapshot", step.Id);
        }
        return new(errors);
    }

    private static WorkflowDefinitionValidationResult ValidateSnapshot(string id, EnvironmentSnapshot? snapshot, string path, string? nodeId)
    {
        var errors = new List<WorkflowDefinitionValidationError>();
        void Error(string message) => errors.Add(new("definition.environment.snapshot.invalid", message, path, nodeId));
        if (string.IsNullOrWhiteSpace(id))
            return new([new("definition.environment.invalid", "Environment ID cannot be empty.", nodeId is null ? "defaultEnvironmentId" : "steps[].environmentId", nodeId)]);
        if (snapshot is null)
        {
            if (id != EnvironmentService.DefaultEnvironmentId) Error($"Environment '{id}' has no pinned snapshot.");
            return new(errors);
        }
        if (snapshot.Id != id || snapshot.Revision < 1 || string.IsNullOrWhiteSpace(snapshot.Name) || snapshot.Name.Length > 120
            || snapshot.Description is null || snapshot.Description.Length > 2000)
            Error("Pinned environment snapshot is malformed or does not match the selected environment.");
        var configuration = ValidateConfiguration(snapshot.Configuration);
        errors.AddRange(configuration.Errors.Select(error => error with { Path = $"{path}.configuration.{error.Path}", NodeId = nodeId }));
        if (id == EnvironmentService.DefaultEnvironmentId &&
            (snapshot.Revision != 1 || snapshot.Name != EnvironmentService.DefaultSnapshot.Name
             || snapshot.Description != EnvironmentService.DefaultSnapshot.Description || snapshot.Configuration?.Runtime?.TimeoutLimitSeconds is not null))
            Error("The built-in default environment must preserve default behavior.");
        return new(errors);
    }

    // Configuration is already validated: schema 1 supports only an optional timeout cap.
    private static bool Equivalent(EnvironmentSnapshot left, EnvironmentSnapshot right) =>
        left.Id == right.Id && left.Revision == right.Revision && left.Name == right.Name && left.Description == right.Description
        && left.Configuration.SchemaVersion == right.Configuration.SchemaVersion
        && left.Configuration.Runtime?.TimeoutLimitSeconds == right.Configuration.Runtime?.TimeoutLimitSeconds;

    public static EnvironmentSnapshot? ResolveForTask(WorkflowDefinitionDocument document, WorkflowDefinitionStep step)
    {
        if (!PersonaDefinitions.IsAiTask(step.Uses)) return null;
        var validation = ValidateRuntime(document);
        if (!validation.IsValid) throw new InvalidOperationException(string.Join(" ", validation.Errors.Select(error => error.Message)));
        return step.EnvironmentSnapshot ?? (step.EnvironmentId == EnvironmentService.DefaultEnvironmentId
            ? EnvironmentService.DefaultSnapshot : document.DefaultEnvironmentSnapshot ?? EnvironmentService.DefaultSnapshot);
    }
}
