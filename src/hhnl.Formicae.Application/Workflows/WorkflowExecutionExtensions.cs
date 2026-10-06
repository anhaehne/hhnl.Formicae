using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace hhnl.Formicae.Application.Workflows;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowSecretReference(string EnvironmentName, string SecretName, string Key);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowScriptSettings(string Script, string Shell = "sh", [property: JsonNumberHandling(JsonNumberHandling.Strict)] int TimeoutSeconds = 300, string WorkingDirectory = "workspace");

/// <summary>Validates managed worker resources; capabilities are not an operating-system sandbox.</summary>
public static class WorkflowExecutionExtensions
{
    public const string ScriptUses = "builtins.script";
    private static readonly Regex Name = new("^[a-z][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant);
    private static readonly Regex EnvName = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex SecretName = new(@"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex SecretKey = new("^[A-Za-z0-9._-]{1,253}$", RegexOptions.CultureInvariant);
    public static bool IsExecutionTask(string? uses) => PersonaDefinitions.IsAiTask(uses) || uses == ScriptUses;
    public static bool ValidName(string? name) => name is not null && Name.IsMatch(name);
    public static bool ValidEnvironmentName(string? name) => name is not null && EnvName.IsMatch(name);
    public static bool ValidSecretName(string? name) => name is not null && name.Length <= 253 && SecretName.IsMatch(name);
    public static bool ValidShell(string? shell) => shell is "sh" or "bash";
    public static bool IsReservedEnvironmentName(string name) => name.StartsWith("FORMICAE_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("LLM_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase)
        || new[] { "PATH", "HOME", "OPENHANDS_PERSISTENCE_DIR", "BASH_ENV", "ENV", "PYTHONPATH", "PYTHONHOME", "NODE_PATH", "GIT_CONFIG", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_COUNT", "NODE_OPTIONS", "DOTNET_STARTUP_HOOKS", "LD_PRELOAD", "LD_LIBRARY_PATH" }.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> ResolveCapabilities(WorkflowDefinitionStep step, EnvironmentSnapshot? environment)
    {
        if (step.Capabilities is not null) return step.Capabilities;
        var result = new List<string>();
        if (step.Uses is "builtins.implement" or "builtins.address-comments") result.AddRange(["browser", "nested-containers"]);
        result.AddRange(environment?.Configuration.Tools.Select(tool => "tool:" + tool.Name) ?? []);
        if (step.Uses != ScriptUses) result.AddRange(environment?.Configuration.McpServers.Select(server => "mcp:" + server.Name) ?? []);
        return result;
    }

    public static WorkflowDefinitionValidationResult ValidateStep(WorkflowDefinitionStep step, EnvironmentSnapshot? environment = null)
    {
        var errors = new List<WorkflowDefinitionValidationError>();
        void Error(string message, string path) => errors.Add(new("definition.execution.invalid", message, "steps[]." + path, step.Id));
        if (step.Uses == ScriptUses)
        {
            if (step.Script is not { } script) Error("Script tasks require script settings.", "script");
            else
            {
                if (string.IsNullOrWhiteSpace(script.Script) || Encoding.UTF8.GetByteCount(script.Script) > 65536) Error("Script must be nonblank and at most 65536 UTF-8 bytes.", "script.script");
                if (!ValidShell(script.Shell)) Error("Script shell must be sh or bash.", "script.shell");
                if (script.TimeoutSeconds is < 1 or > 3600) Error("Script timeout must be between 1 and 3600 seconds.", "script.timeoutSeconds");
                if (script.WorkingDirectory is not ("workspace" or "repository")) Error("Working directory must be workspace or repository.", "script.workingDirectory");
            }
            if (step.AiSettingsId is not null || step.Model is not null) Error("Script tasks cannot select AI settings or a model.", "aiSettingsId");
        }
        else if (step.Script is not null) Error("Only Script tasks may carry script settings.", "script");
        if (!IsExecutionTask(step.Uses))
        {
            if (step.Capabilities is not null || step.SecretReferences is not null) Error("Only AI or Script tasks may select capabilities and secrets.", "capabilities");
            return new(errors);
        }
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var duplicateAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (step.SecretReferences?.Count > 64) Error("At most 64 secret references are allowed per task.", "secretReferences");
        foreach (var reference in step.SecretReferences ?? [])
        {
            if (reference is null) { Error("Secret references cannot be null.", "secretReferences"); continue; }
            if (!ValidEnvironmentName(reference.EnvironmentName) || IsReservedEnvironmentName(reference.EnvironmentName)) Error("Secret environment alias is invalid or reserved.", "secretReferences.environmentName");
            else if (!duplicateAliases.Add(reference.EnvironmentName)) Error("Secret environment aliases must be unique.", "secretReferences.environmentName");
            aliases.Add(reference.EnvironmentName);
            if (!ValidSecretName(reference.SecretName)) Error("Secret name must be a Kubernetes DNS subdomain.", "secretReferences.secretName");
            if (reference.Key is null || !SecretKey.IsMatch(reference.Key)) Error("Secret key must contain only letters, digits, dots, underscores or hyphens.", "secretReferences.key");
        }
        var selected = new HashSet<string>(StringComparer.Ordinal);
        if (step.Capabilities?.Count > 66) Error("Too many task capabilities.", "capabilities");
        foreach (var capability in step.Capabilities ?? [])
        {
            if (capability is null || !selected.Add(capability)) { Error("Capabilities must be non-null and unique.", "capabilities"); continue; }
            var isTool = capability.StartsWith("tool:", StringComparison.Ordinal) && ValidName(capability[5..]);
            var isMcp = capability.StartsWith("mcp:", StringComparison.Ordinal) && ValidName(capability[4..]);
            if (capability is not ("browser" or "nested-containers") && !isTool && !isMcp) Error("Unknown managed capability.", "capabilities");
            if (step.Uses == ScriptUses && !isTool) Error("Script tasks support tool capabilities only.", "capabilities");
            if (environment is not null && isTool && !environment.Configuration.Tools.Any(tool => tool.Name == capability[5..])) Error("Selected tool is absent from the pinned environment.", "capabilities");
            if (environment is not null && isMcp && !environment.Configuration.McpServers.Any(server => server.Name == capability[4..])) Error("Selected MCP server is absent from the pinned environment.", "capabilities");
        }
        if (environment is not null)
        {
            var capabilities = ResolveCapabilities(step, environment);
            foreach (var server in environment.Configuration.McpServers.Where(server => capabilities.Contains("mcp:" + server.Name)))
            foreach (var alias in (server.EnvironmentVariables?.Values ?? []).Concat(server.HeaderEnvironmentVariables?.Values ?? []).Concat(server.BearerTokenEnvironmentVariable is null ? [] : new[] { server.BearerTokenEnvironmentVariable }))
                if (!aliases.Contains(alias)) Error($"Enabled MCP server '{server.Name}' requires selected task secret alias '{alias}'.", "secretReferences");
        }
        return new(errors);
    }
}
