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
        var id = document.DefaultEnvironmentId ?? EnvironmentService.DefaultEnvironmentId;
        EnvironmentSnapshot? snapshot = null;
        if (id == EnvironmentService.DefaultEnvironmentId) snapshot = EnvironmentService.DefaultSnapshot;
        else if (!string.IsNullOrWhiteSpace(id) && environments is not null)
        {
            var environment = await environments.GetAsync(id, token);
            if (environment is not null)
                snapshot = new(environment.Id, environment.Revision, environment.Name, environment.Description, environment.Configuration);
        }
        var enriched = document with { DefaultEnvironmentSnapshot = snapshot };
        if (snapshot is null)
            return new(enriched, new([new("definition.environment.missing", $"Workflow environment '{id}' is unavailable.", "defaultEnvironmentId")]));
        return new(enriched, ValidateRuntime(enriched));
    }

    public static WorkflowDefinitionValidationResult ValidateRuntime(WorkflowDefinitionDocument document)
    {
        var errors = new List<WorkflowDefinitionValidationError>();
        void Error(string message) => errors.Add(new("definition.environment.snapshot.invalid", message, "defaultEnvironmentSnapshot"));
        var id = document.DefaultEnvironmentId ?? EnvironmentService.DefaultEnvironmentId;
        if (string.IsNullOrWhiteSpace(id))
            return new([new("definition.environment.invalid", "Workflow environment ID cannot be empty.", "defaultEnvironmentId")]);
        var snapshot = document.DefaultEnvironmentSnapshot;
        if (snapshot is null)
        {
            if (id != EnvironmentService.DefaultEnvironmentId) Error($"Environment '{id}' has no pinned snapshot.");
            return new(errors);
        }
        if (snapshot.Id != id || snapshot.Revision < 1 || string.IsNullOrWhiteSpace(snapshot.Name) || snapshot.Name.Length > 120
            || snapshot.Description is null || snapshot.Description.Length > 2000)
            Error("Pinned environment snapshot is malformed or does not match the selected environment.");
        var configuration = ValidateConfiguration(snapshot.Configuration);
        errors.AddRange(configuration.Errors.Select(error => error with { Path = $"defaultEnvironmentSnapshot.configuration.{error.Path}" }));
        if (id == EnvironmentService.DefaultEnvironmentId &&
            (snapshot.Revision != 1 || snapshot.Name != EnvironmentService.DefaultSnapshot.Name
             || snapshot.Description != EnvironmentService.DefaultSnapshot.Description || snapshot.Configuration?.Runtime?.TimeoutLimitSeconds is not null))
            Error("The built-in default environment must preserve default behavior.");
        return new(errors);
    }

    public static EnvironmentSnapshot? ResolveForTask(WorkflowDefinitionDocument document, WorkflowDefinitionStep step)
    {
        if (!PersonaDefinitions.IsAiTask(step.Uses)) return null;
        var validation = ValidateRuntime(document);
        if (!validation.IsValid) throw new InvalidOperationException(string.Join(" ", validation.Errors.Select(error => error.Message)));
        return document.DefaultEnvironmentSnapshot ?? EnvironmentService.DefaultSnapshot;
    }
}
