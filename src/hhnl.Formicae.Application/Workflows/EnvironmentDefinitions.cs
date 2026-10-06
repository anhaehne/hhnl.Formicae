using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

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
        if (configuration.Image is { } image)
        {
            if (!EnvironmentImageReferences.IsValid(image.Reference)) Error("Image reference requires a lowercase Docker repository name, an optional tag of at most 128 characters, or a SHA-256 digest of 64 hexadecimal characters.", "image.reference");
            if (image.PullPolicy is not ("Always" or "IfNotPresent" or "Never")) Error("Image pull policy must be Always, IfNotPresent or Never.", "image.pullPolicy");
            if (image.PullSecretNames?.Count > 16 || image.PullSecretNames?.Any(name => !WorkflowExecutionExtensions.ValidSecretName(name)) == true || image.PullSecretNames?.Distinct(StringComparer.Ordinal).Count() != image.PullSecretNames?.Count) Error("Image pull secret names must be unique Kubernetes secret names (at most 16).", "image.pullSecretNames");
        }
        if (configuration.Tools is null) Error("Environment tools must be an array.", "tools");
        else
        {
            if (configuration.Tools.Count > 32) Error("At most 32 tools are allowed.", "tools");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in configuration.Tools)
            {
                if (tool is null) { Error("Tool entries cannot be null.", "tools"); continue; }
                if (!WorkflowExecutionExtensions.ValidName(tool.Name) || !names.Add(tool.Name)) Error("Tool names must be unique lowercase names.", "tools.name");
                if (string.IsNullOrWhiteSpace(tool.Script) || Encoding.UTF8.GetByteCount(tool.Script) > 65536) Error("Tool install script must be nonblank and at most 65536 UTF-8 bytes.", "tools.script");
                if (!WorkflowExecutionExtensions.ValidShell(tool.Shell)) Error("Tool shell must be sh or bash.", "tools.shell");
                if (tool.TimeoutSeconds is < 1 or > 3600) Error("Tool timeout must be between 1 and 3600 seconds.", "tools.timeoutSeconds");
            }
        }
        if (configuration.McpServers is null) Error("Environment MCP servers must be an array.", "mcpServers");
        else
        {
            if (configuration.McpServers.Count > 32) Error("At most 32 MCP servers are allowed.", "mcpServers");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var server in configuration.McpServers)
            {
                if (server is null) { Error("MCP server entries cannot be null.", "mcpServers"); continue; }
                if (!WorkflowExecutionExtensions.ValidName(server.Name) || server.Name == "playwright" || !names.Add(server.Name)) Error("MCP server names must be unique lowercase names; playwright is reserved for the browser capability.", "mcpServers.name");
                if (server.Transport is not ("stdio" or "http")) Error("MCP transport must be stdio or http (Streamable HTTP).", "mcpServers.transport");
                if (server.Transport == "stdio" && (string.IsNullOrWhiteSpace(server.Command) || server.Command.Length > 512 || server.Command.Any(char.IsControl) || server.Url is not null || server.BearerTokenEnvironmentVariable is not null || server.HeaderEnvironmentVariables?.Count > 0)) Error("Stdio MCP servers require a command and cannot have URL or HTTP credentials.", "mcpServers.command");
                if (server.Arguments?.Count > 64 || server.Arguments?.Any(arg => arg is null || arg.Length > 4096 || arg.Contains('\0')) == true) Error("MCP arguments must contain at most 64 bounded strings.", "mcpServers.arguments");
                if (server.Transport == "http" && server.EnvironmentVariables is { Count: > 0 }) Error("HTTP MCP servers use bearer token or header aliases; environment variables are supported only by stdio servers.", "mcpServers.environmentVariables");
                if (server.Transport == "http" && (server.Command is not null || server.Arguments?.Count > 0 || !Uri.TryCreate(server.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0)) Error("HTTP MCP servers require an HTTP(S) URL without embedded credentials, query or fragment, and cannot have a command or arguments.", "mcpServers.url");
                if (server.EnvironmentVariables?.Count > 64 || server.EnvironmentVariables?.Any(pair => !WorkflowExecutionExtensions.ValidEnvironmentName(pair.Key) || WorkflowExecutionExtensions.IsReservedEnvironmentName(pair.Key) || !WorkflowExecutionExtensions.ValidEnvironmentName(pair.Value) || WorkflowExecutionExtensions.IsReservedEnvironmentName(pair.Value)) == true) Error("MCP environment variables map nonreserved environment names to selected step secret aliases.", "mcpServers.environmentVariables");
                if (server.BearerTokenEnvironmentVariable is { } bearer && (!WorkflowExecutionExtensions.ValidEnvironmentName(bearer) || WorkflowExecutionExtensions.IsReservedEnvironmentName(bearer))) Error("Bearer token must refer to a selected step secret alias.", "mcpServers.bearerTokenEnvironmentVariable");
                if (server.BearerTokenEnvironmentVariable is not null && server.HeaderEnvironmentVariables?.Keys.Any(header => header.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) == true) Error("Configure either a bearer token alias or an Authorization header alias, not both.", "mcpServers.headerEnvironmentVariables");
                if (server.HeaderEnvironmentVariables?.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != server.HeaderEnvironmentVariables?.Count) Error("MCP header names must be unique regardless of case.", "mcpServers.headerEnvironmentVariables");
                if (server.HeaderEnvironmentVariables?.Count > 32 || server.HeaderEnvironmentVariables?.Any(pair => !Regex.IsMatch(pair.Key, "^[A-Za-z0-9-]{1,128}$") || !WorkflowExecutionExtensions.ValidEnvironmentName(pair.Value) || WorkflowExecutionExtensions.IsReservedEnvironmentName(pair.Value)) == true) Error("MCP headers map valid header names to selected step secret aliases.", "mcpServers.headerEnvironmentVariables");
            }
        }
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
            if (!WorkflowExecutionExtensions.IsExecutionTask(step.Uses))
            {
                if (step.EnvironmentId is not null)
                    errors.Add(new("definition.environment.unsupported", "Only AI or Script tasks can select an environment.", "steps[].environmentId", step.Id));
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
            if (!WorkflowExecutionExtensions.IsExecutionTask(step.Uses))
            {
                if (step.EnvironmentId is not null || step.EnvironmentSnapshot is not null)
                    errors.Add(new("definition.environment.unsupported", "Only AI or Script tasks may have an environment selection or snapshot.", "steps[].environmentId", step.Id));
                continue;
            }
            var id = step.EnvironmentId ?? defaultId;
            // Metadata-free nodes from 0.16.0 inherit the document's immutable snapshot.
            var snapshot = step.EnvironmentSnapshot ?? (step.EnvironmentId is null ? defaultSnapshot : null);
            var pinned = Check(id, snapshot, "steps[].environmentSnapshot", step.Id);
            if (pinned is not null) errors.AddRange(WorkflowExecutionExtensions.ValidateStep(step, pinned).Errors);
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
             || snapshot.Description != EnvironmentService.DefaultSnapshot.Description || !ConfigurationEquivalent(snapshot.Configuration, EnvironmentService.DefaultSnapshot.Configuration)))
            Error("The built-in default environment must preserve default behavior.");
        return new(errors);
    }

    private static bool ConfigurationEquivalent(EnvironmentConfiguration? left, EnvironmentConfiguration? right)
    {
        static bool Equal(JsonElement a, JsonElement b)
        {
            if (a.ValueKind != b.ValueKind) return false;
            if (a.ValueKind == JsonValueKind.Object)
            {
                var properties = a.EnumerateObject().ToArray();
                return properties.Length == b.EnumerateObject().Count()
                    && properties.All(property => b.TryGetProperty(property.Name, out var value) && Equal(property.Value, value));
            }
            if (a.ValueKind == JsonValueKind.Array)
            {
                var aa = a.EnumerateArray().ToArray(); var bb = b.EnumerateArray().ToArray();
                return aa.Length == bb.Length && aa.Zip(bb).All(pair => Equal(pair.First, pair.Second));
            }
            return a.GetRawText() == b.GetRawText();
        }
        object? Normalize(EnvironmentConfiguration? configuration) => configuration is null ? null : new
        {
            configuration.SchemaVersion, TimeoutLimitSeconds = configuration.Runtime?.TimeoutLimitSeconds,
            Image = configuration.Image is { } image ? new { image.Reference, image.PullPolicy, PullSecretNames = image.PullSecretNames ?? [] } : null,
            configuration.Tools,
            McpServers = configuration.McpServers?.Select(server => server is null ? null : new
            {
                server.Name, server.Transport, server.Command, Arguments = server.Arguments ?? [], server.Url,
                EnvironmentVariables = server.EnvironmentVariables ?? new Dictionary<string, string>(),
                server.BearerTokenEnvironmentVariable, HeaderEnvironmentVariables = server.HeaderEnvironmentVariables ?? new Dictionary<string, string>()
            }).ToArray()
        };
        return Equal(JsonSerializer.SerializeToElement(Normalize(left), JsonOptions), JsonSerializer.SerializeToElement(Normalize(right), JsonOptions));
    }

    private static bool Equivalent(EnvironmentSnapshot left, EnvironmentSnapshot right) =>
        left.Id == right.Id && left.Revision == right.Revision && left.Name == right.Name && left.Description == right.Description
        && ConfigurationEquivalent(left.Configuration, right.Configuration);

    public static EnvironmentSnapshot? ResolveForTask(WorkflowDefinitionDocument document, WorkflowDefinitionStep step)
    {
        if (!WorkflowExecutionExtensions.IsExecutionTask(step.Uses)) return null;
        var validation = ValidateRuntime(document);
        if (!validation.IsValid) throw new InvalidOperationException(string.Join(" ", validation.Errors.Select(error => error.Message)));
        return step.EnvironmentSnapshot ?? (step.EnvironmentId == EnvironmentService.DefaultEnvironmentId
            ? EnvironmentService.DefaultSnapshot : document.DefaultEnvironmentSnapshot ?? EnvironmentService.DefaultSnapshot);
    }
}
