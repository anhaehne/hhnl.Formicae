using System.Security.Cryptography;
using System.Text.Json;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Application.Workflows;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Infrastructure.OpenHands;

public sealed class OpenHandsAgentRunner : IAgentRunner
{
    private static readonly IReadOnlyList<string> WorkerCommand = ["dotnet", "hhnl.Formicae.Worker.dll"];

    private readonly IJobRuntime jobRuntime;
    private readonly IOptions<RuntimeJobOptions> jobOptions;
    private readonly IOptions<OpenHandsOptions> openHandsOptions;
    private readonly AiSettingsService? aiSettingsService;
    private readonly IDevOpsIntegrationStore? integrationStore;
    private readonly IGitHubAppClient? gitHubAppClient;

    public OpenHandsAgentRunner(IJobRuntime jobRuntime, IOptions<RuntimeJobOptions> jobOptions, IOptions<OpenHandsOptions> openHandsOptions)
        : this(jobRuntime, jobOptions, openHandsOptions, null)
    {
    }

    public OpenHandsAgentRunner(IJobRuntime jobRuntime, IOptions<RuntimeJobOptions> jobOptions, IOptions<OpenHandsOptions> openHandsOptions, AiSettingsService? aiSettingsService, IDevOpsIntegrationStore? integrationStore = null, IGitHubAppClient? gitHubAppClient = null)
    {
        this.jobRuntime = jobRuntime;
        this.jobOptions = jobOptions;
        this.openHandsOptions = openHandsOptions;
        this.aiSettingsService = aiSettingsService;
        this.integrationStore = integrationStore;
        this.gitHubAppClient = gitHubAppClient;
    }

    public async Task<AgentRunStartResult> StartAsync(AgentTask task, CancellationToken cancellationToken)
    {
        if (task.Kind == TaskRunKind.Script)
        {
            var scriptSpec = BuildScriptSpec(task, await CreateGitAccessTokenAsync(task, cancellationToken));
            RuntimeJobStartResult scriptStart;
            try { scriptStart = await jobRuntime.StartJobAsync(scriptSpec, cancellationToken); }
            catch (Exception error) when (task.ExecutionAttemptId is not null && !cancellationToken.IsCancellationRequested && IsUncertainLaunch(error))
            { throw new AgentLaunchUncertainException("Script runtime launch outcome is unknown; this attempt can be resumed.", error); }
            return new(scriptStart.ExternalId);
        }
        var settings = aiSettingsService is null
            ? string.IsNullOrWhiteSpace(task.AiSettingsId) ? ResolveSettingsFromOptions(openHandsOptions.Value) : throw new InvalidOperationException("Named AI configurations require the AI settings service.")
            : string.IsNullOrWhiteSpace(task.AiSettingsId)
                ? await aiSettingsService.ResolveAsync(cancellationToken)
                : await aiSettingsService.ResolveAsync(task.AiSettingsId, cancellationToken)
                    ?? throw new InvalidOperationException($"AI configuration '{task.AiSettingsId}' was not found.");
        if (settings.AgentKind == AgentKinds.Acp && !UsesCodexCli(settings))
            throw new InvalidOperationException("ACP agent execution is not supported by this worker.");
        var gitAccessToken = await CreateGitAccessTokenAsync(task, cancellationToken);
        var spec = BuildSpec(task, settings, gitAccessToken);
        RuntimeJobStartResult start;
        try
        {
            start = await jobRuntime.StartJobAsync(spec, cancellationToken);
        }
        catch (Exception exception) when (task.ExecutionAttemptId is not null
            && !cancellationToken.IsCancellationRequested && IsUncertainLaunch(exception))
        {
            throw new AgentLaunchUncertainException("The runtime launch outcome is unknown; the same execution attempt can be resumed.", exception);
        }
        return new AgentRunStartResult(start.ExternalId, AiSettingsId: settings.Id, Model: TrimToNull(ResolveModel(task, settings, ResolveAuthMethod(settings.AuthMethod))));
    }

    private static bool IsUncertainLaunch(Exception exception)
    {
        static bool Transient(int? status) => status is 408 or 429 or >= 500;
        return exception switch
        {
            k8s.Autorest.HttpOperationException operation => Transient((int?)operation.Response?.StatusCode),
            k8s.KubernetesException kubernetes => Transient(kubernetes.Status?.Code),
            HttpRequestException http => http.StatusCode is null || Transient((int)http.StatusCode),
            TimeoutException or OperationCanceledException or System.Net.Sockets.SocketException => true,
            _ => false
        };
    }

    internal static bool UsesCodexCli(ResolvedAiSettings settings)
        => settings.AuthMethod == OpenHandsAuthMethods.CodexSubscription
            && (settings.AgentKind != AgentKinds.Acp || settings.AcpProvider == AcpProviders.Codex);

    public async Task<AgentRunResult?> TryGetResultAsync(string externalId, CancellationToken cancellationToken)
    {
        var result = await jobRuntime.TryGetJobResultAsync(externalId, cancellationToken);
        if (result is null) return null;
        var rawLogs = UnwrapRuntimeLogs(result.Logs);
        if (externalId.StartsWith("formicae-custom-", StringComparison.Ordinal) && TryReadCustomResult(result.Logs, out var custom) && custom is not null)
            return new(result.Succeeded && custom.Succeeded, result.ExternalId, custom.Output ?? "",
                custom.Succeeded ? result.FailureReason : custom.FailureReason, OutputIsFinalResponse: custom.Succeeded, ExitCode: result.ExitCode);
        if (externalId.StartsWith("formicae-script-", StringComparison.Ordinal) && TryReadScriptResult(result.Logs, out var scriptOutput, out var scriptExit, out var scriptFailure))
            return new(result.Succeeded && scriptExit == 0, result.ExternalId, scriptOutput,
                scriptExit == 0 ? result.FailureReason : scriptFailure ?? $"Script exited with code {scriptExit}.", ExitCode: scriptExit);
        if (externalId.StartsWith("formicae-script-", StringComparison.Ordinal) && result.Succeeded)
            return new(false, result.ExternalId, rawLogs, "Worker completed without a valid script result; use a compatible Formicae worker image.", ExitCode: result.ExitCode);
        var finalResponse = result.Succeeded ? ExtractFinalResponse(rawLogs, externalId.StartsWith("formicae-custom-", StringComparison.Ordinal)) : null;
        var output = finalResponse ?? rawLogs;
        var failureReason = result.Succeeded ? null : ExtractCheckpointFailure(rawLogs) ?? result.FailureReason;
        return new AgentRunResult(result.Succeeded, result.ExternalId, output, failureReason, OutputIsFinalResponse: finalResponse is not null, ExitCode: result.ExitCode);
    }

    public async Task<IReadOnlyList<AgentRuntimeLog>> ReadLogsAsync(string externalId, CancellationToken cancellationToken)
    {
        var logs = await jobRuntime.ReadJobLogsAsync(externalId, cancellationToken);
        var result = new List<AgentRuntimeLog>();
        var lineNumber = 0;
        foreach (var line in logs.Split('\n'))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (TryReadRuntimeLog(line, out var recovered))
            {
                for (var offset = 0; offset < recovered!.Message.Length; offset += 16000)
                {
                    var chunk = recovered.Message.Substring(offset, Math.Min(16000, recovered.Message.Length - offset));
                    var id = offset == 0 ? recovered.MessageId : new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{recovered.MessageId}:{offset}")).AsSpan(0, 16));
                    result.Add(recovered with { Message = chunk, MessageId = id, SourceSequence = offset == 0 ? recovered.SourceSequence : null });
                }
                continue;
            }
            // Legacy workers have no identities or stream metadata. Preserve that distinction explicitly.
            for (var offset = 0; offset < line.Length; offset += 16000)
            {
                var chunk = line.Substring(offset, Math.Min(16000, line.Length - offset));
                var identity = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{externalId}:{lineNumber}:{offset}:{chunk}"));
                result.Add(new AgentRuntimeLog("runtime", chunk, DateTimeOffset.UtcNow, new Guid(identity.AsSpan(0, 16))));
            }
        }
        return result;
    }

    public Task CancelAsync(string externalId, CancellationToken cancellationToken)
        => jobRuntime.CancelJobAsync(externalId, cancellationToken);

    public Task AcknowledgeCompletionAsync(string externalId, CancellationToken cancellationToken)
        => jobRuntime.AcknowledgeCompletionAsync(externalId, cancellationToken);

    public string? ResolveExternalId(Guid workflowId, TaskRunKind kind, Guid? attemptId)
        => attemptId is null ? null : BuildJobName(new AgentTask(workflowId, kind, "", "", "", null, ExecutionAttemptId: attemptId));

    internal static string UnwrapRuntimeLogs(string logs)
        => string.Join('\n', logs.Split('\n').Select(line => TryReadRuntimeLog(line, out var entry) ? entry!.Message : line));

    private static bool TryReadRuntimeLog(string line, out AgentRuntimeLog? entry)
    {
        entry = null;
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("formicaeLog", out var marker)
                || marker.ValueKind != JsonValueKind.Number || !marker.TryGetInt32(out var version) || version != 1
                || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("line", out var message) || message.ValueKind != JsonValueKind.String
                || !data.TryGetProperty("stream", out var source) || source.ValueKind != JsonValueKind.String
                || !data.TryGetProperty("messageId", out var id) || id.ValueKind != JsonValueKind.String || !id.TryGetGuid(out var messageId)
                || !data.TryGetProperty("timestamp", out var time) || time.ValueKind != JsonValueKind.String || !time.TryGetDateTimeOffset(out var timestamp)) return false;
            long? sequence = data.TryGetProperty("sequence", out var seq) && seq.ValueKind == JsonValueKind.Number && seq.TryGetInt64(out var value) ? value : null;
            entry = new AgentRuntimeLog(source.GetString()!, message.GetString()!, timestamp, messageId, sequence);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private RuntimeJobSpec BuildSpec(AgentTask task, ResolvedAiSettings settings, string? gitAccessToken)
    {
        var capabilities = ResolveTaskCapabilities(task);
        if (task.EnvironmentSnapshot is { } profile)
        {
            var validation = EnvironmentDefinitions.ValidateConfiguration(profile.Configuration);
            if (!validation.IsValid)
                throw new InvalidOperationException(string.Join(" ", validation.Errors.Select(error => error.Message)));
        }
        var authMethod = ResolveAuthMethod(settings.AuthMethod);
        var model = ResolveModel(task, settings, authMethod);
        var jobName = BuildJobName(task);
        var environment = BuildEnvironment(task, jobName, model, settings, authMethod, jobOptions.Value, gitAccessToken);
        var secretFiles = BuildSecretFiles(jobName, settings, authMethod, jobOptions.Value);
        var secretEnvironment = BuildSecretEnvironment(jobName, settings, authMethod);
        AddExecutionConfiguration(environment, task, capabilities);
        return new RuntimeJobSpec(
            jobName,
            task.EnvironmentSnapshot?.Configuration.Image?.Reference ?? jobOptions.Value.Image,
            environment,
            WorkerCommand,
            ToRuntimeAuthMethod(authMethod),
            task.ContextFiles?.Select(file => new RuntimeJobContextFile(file.FileName, file.Content)).ToArray(),
            SecretFiles: secretFiles,
            SecretEnvironment: secretEnvironment,
            ExecutionRequirements: new(capabilities.Contains("browser"), capabilities.Contains("nested-containers")),
            ExecutionPolicy: task.Kind == TaskRunKind.Custom
                ? new RuntimeJobExecutionPolicy(task.TimeoutSeconds is >= 1 and <= 3600
                    ? task.TimeoutSeconds.Value : throw new InvalidOperationException("Custom tasks require a timeout between 1 and 3600 seconds."), 0)
                : BuildExecutionPolicy(task.Kind, jobOptions.Value),
            ReuseExisting: task.ExecutionAttemptId is not null,
            TimeoutLimitSeconds: task.EnvironmentSnapshot?.Configuration.Runtime?.TimeoutLimitSeconds,
            ImagePullPolicy: task.EnvironmentSnapshot?.Configuration.Image?.PullPolicy ?? "IfNotPresent",
            ImagePullSecretNames: task.EnvironmentSnapshot?.Configuration.Image?.PullSecretNames,
            SecretReferences: task.SecretReferences);
    }

    private RuntimeJobSpec BuildScriptSpec(AgentTask task, string? gitAccessToken)
    {
        var capabilities = ResolveTaskCapabilities(task);
        var script = task.Script ?? throw new InvalidOperationException("Script settings are required.");
        var name = BuildJobName(task);
        var environment = new Dictionary<string, string>
        {
            ["FORMICAE_WORKFLOW_ID"] = task.WorkflowId.ToString("D"), ["FORMICAE_TASK_KIND"] = "Script",
            ["FORMICAE_REPOSITORY_URL"] = task.RepositoryUrl, ["FORMICAE_BRANCH"] = task.BranchName,
            ["FORMICAE_TASK_PROMPT"] = "Execute the configured workflow script.", ["FORMICAE_EXTERNAL_ID"] = name,
            ["FORMICAE_OPENHANDS_AUTH_METHOD"] = RuntimeJobAuthMethods.None,
            ["FORMICAE_SCRIPT_SETTINGS"] = JsonSerializer.Serialize(script, JsonSerializerOptions.Web)
        };
        if (task.ExecutionAttemptId is { } attempt) environment["FORMICAE_EXECUTION_ATTEMPT_ID"] = attempt.ToString("D");
        if (!string.IsNullOrWhiteSpace(gitAccessToken)) environment["FORMICAE_GIT_ACCESS_TOKEN"] = gitAccessToken;
        if (!string.IsNullOrWhiteSpace(jobOptions.Value.WorkerCallbackUrl)) environment["FORMICAE_WORKER_CALLBACK_URL"] = jobOptions.Value.WorkerCallbackUrl;
        if (!string.IsNullOrWhiteSpace(jobOptions.Value.WorkerCallbackSecret)) environment["FORMICAE_WORKER_CALLBACK_SECRET"] = jobOptions.Value.WorkerCallbackSecret;
        AddExecutionConfiguration(environment, task, capabilities);
        var configuration = task.EnvironmentSnapshot?.Configuration;
        return new(name, configuration?.Image?.Reference ?? jobOptions.Value.Image, environment, WorkerCommand,
            AuthMethod: RuntimeJobAuthMethods.None, ExecutionRequirements: new(),
            ExecutionPolicy: new(script.TimeoutSeconds, StartupGraceSeconds: 30), ReuseExisting: task.ExecutionAttemptId is not null,
            TimeoutLimitSeconds: configuration?.Runtime?.TimeoutLimitSeconds, ImagePullPolicy: configuration?.Image?.PullPolicy ?? "IfNotPresent",
            ImagePullSecretNames: configuration?.Image?.PullSecretNames, SecretReferences: task.SecretReferences);
    }

    private static IReadOnlyList<string> ResolveTaskCapabilities(AgentTask task)
    {
        var uses = task.Kind switch
        {
            TaskRunKind.Script => WorkflowExecutionExtensions.ScriptUses, TaskRunKind.Custom => CustomTaskDefinitions.Uses,
            TaskRunKind.Implement => "builtins.implement", TaskRunKind.AddressComments => "builtins.address-comments", _ => "builtins.plan"
        };
        var step = new WorkflowDefinitionStep("launch", uses, Capabilities: task.Capabilities, SecretReferences: task.SecretReferences, Script: task.Script);
        var validation = WorkflowExecutionExtensions.ValidateStep(step, task.EnvironmentSnapshot);
        if (task.EnvironmentSnapshot is { } profile)
            validation = new([.. validation.Errors, .. EnvironmentDefinitions.ValidateConfiguration(profile.Configuration).Errors]);
        if (!validation.IsValid) throw new InvalidOperationException(string.Join(" ", validation.Errors.Select(error => error.Message)));
        return WorkflowExecutionExtensions.ResolveCapabilities(step, task.EnvironmentSnapshot);
    }

    private static void AddExecutionConfiguration(Dictionary<string, string> environment, AgentTask task, IReadOnlyList<string> capabilities)
    {
        var configuration = task.EnvironmentSnapshot?.Configuration ?? new EnvironmentConfiguration();
        configuration = configuration with
        {
            Tools = configuration.Tools.Where(tool => capabilities.Contains("tool:" + tool.Name)).ToArray(),
            McpServers = configuration.McpServers.Where(server => capabilities.Contains("mcp:" + server.Name)).ToArray()
        };
        environment["FORMICAE_EXECUTION_CONFIGURATION"] = JsonSerializer.Serialize(configuration, JsonSerializerOptions.Web);
        environment["FORMICAE_SECRET_ENVIRONMENT_NAMES"] = JsonSerializer.Serialize((task.SecretReferences ?? []).Select(reference => reference.EnvironmentName));
        environment["FORMICAE_CAPABILITIES"] = JsonSerializer.Serialize(capabilities);
    }

    internal static bool TryReadCustomResult(string logs, out AgentTaskOutputResult? result)
    {
        result = null;
        foreach (var line in logs.Split('\n').Reverse())
        {
            try
            {
                if (!TryReadRuntimeLog(line, out var entry) || entry!.Source != "worker") continue;
                using var json = JsonDocument.Parse(entry.Message);
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("formicaeCustomResult", out var marker)
                    || marker.ValueKind != JsonValueKind.Object) continue;
                result = marker.Deserialize<AgentTaskOutputResult>(JsonSerializerOptions.Web);
                if (result is not null && (result.Succeeded ? result.Output is not null : !string.IsNullOrWhiteSpace(result.FailureReason))) return true;
            }
            catch (JsonException) { }
        }
        result = null;
        return false;
    }

    internal static bool TryReadScriptResult(string logs, out string output, out int exitCode, out string? failureReason)
    {
        output = ""; exitCode = 0; failureReason = null;
        foreach (var line in logs.Split('\n').Reverse())
        {
            try
            {
                if (!TryReadRuntimeLog(line, out var entry) || entry!.Source != "worker") continue;
                using var json = JsonDocument.Parse(entry.Message);
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("formicaeScriptResult", out var result)
                    || result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("exitCode", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out exitCode)
                    || !result.TryGetProperty("output", out var text) || text.ValueKind != JsonValueKind.String) continue;
                output = text.GetString() ?? "";
                if (result.TryGetProperty("failureReason", out var reason) && reason.ValueKind == JsonValueKind.String) failureReason = reason.GetString();
                return true;
            }
            catch (JsonException) { }
        }
        return false;
    }

    private static RuntimeJobExecutionRequirements BuildExecutionRequirements(TaskRunKind taskKind)
        => taskKind is TaskRunKind.Implement or TaskRunKind.AddressComments
            ? new RuntimeJobExecutionRequirements(RequiresBrowser: true, RequiresNestedContainers: true)
            : new RuntimeJobExecutionRequirements();

    private static RuntimeJobExecutionPolicy? BuildExecutionPolicy(TaskRunKind taskKind, RuntimeJobOptions options)
        => taskKind is TaskRunKind.Implement or TaskRunKind.AddressComments
            ? new RuntimeJobExecutionPolicy(
                Math.Max(1, options.ImplementationTimeoutSeconds),
                Math.Clamp(options.ImplementationCheckpointGraceSeconds, 0, Math.Max(0, options.ImplementationTimeoutSeconds - 1)))
            : null;

    private async Task<string?> CreateGitAccessTokenAsync(AgentTask task, CancellationToken cancellationToken)
    {
        if (!(task.Kind is TaskRunKind.Plan or TaskRunKind.Implement or TaskRunKind.AddressComments
            || task.Kind == TaskRunKind.Script && task.Script?.WorkingDirectory == "repository") || integrationStore is null || gitHubAppClient is null) return null;
        var connectedRepository = await integrationStore.GetRepositoryByUrlAsync(task.RepositoryUrl, cancellationToken);
        if (connectedRepository?.InstallationId is not { } installationId) return null;
        var integration = connectedRepository.DevOpsIntegration ?? await integrationStore.GetAsync(connectedRepository.DevOpsIntegrationId, cancellationToken);
        return integration is null ? null : await gitHubAppClient.CreateInstallationTokenAsync(integration, installationId, cancellationToken);
    }

    private static string BuildJobName(AgentTask task)
    {
        if (task.ExecutionAttemptId is { } attempt)
        {
            // The durable attempt, not prompt content or a per-call nonce, owns the external Job.
            var identity = $"{task.WorkflowId:N}:{task.Kind}:{attempt:N}";
            var attemptHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))[..32].ToLowerInvariant();
            return $"formicae-{task.Kind.ToString().ToLowerInvariant()}-{attemptHash}";
        }
        var prefix = $"formicae-{task.Kind.ToString().ToLowerInvariant()}-{task.WorkflowId:N}";
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(task.Prompt)))[..8].ToLowerInvariant();
        var nonce = Guid.NewGuid().ToString("N")[..8];
        var suffix = $"-{hash}-{nonce}";
        var maxPrefixLength = 63 - suffix.Length;
        return $"{prefix[..Math.Min(prefix.Length, maxPrefixLength)]}{suffix}";
    }

    internal static string? ExtractFinalResponse(string logs, bool includeOpenHands = true)
    {
        logs = UnwrapRuntimeLogs(logs);
        string? lastMessage = null;
        string? terminalResponse = null;
        var hasTerminalResponse = false;
        foreach (var line in logs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (includeOpenHands && TryGetString(root, "kind", out var kind)
                    && TryGetString(root, "source", out var sdkSource) && sdkSource == "agent")
                {
                    if (kind == "ActionEvent" && root.TryGetProperty("action", out var sdkAction) && sdkAction.ValueKind == JsonValueKind.Object
                        && TryGetString(sdkAction, "kind", out var actionKind) && actionKind == "FinishAction")
                    {
                        hasTerminalResponse = true;
                        terminalResponse = TryGetString(sdkAction, "message", out var finishMessage) ? finishMessage : null;
                    }
                    else if (kind == "MessageEvent" && root.TryGetProperty("llm_message", out var llm) && llm.ValueKind == JsonValueKind.Object
                        && TryGetString(llm, "role", out var role) && role == "assistant"
                        && llm.TryGetProperty("content", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    {
                        var textParts = parts.EnumerateArray().Where(part => part.ValueKind == JsonValueKind.Object
                            && TryGetString(part, "type", out var partType) && partType == "text"
                            && part.TryGetProperty("text", out var partText) && partText.ValueKind == JsonValueKind.String)
                            .Select(part => part.GetProperty("text").GetString());
                        lastMessage = string.Concat(textParts);
                    }
                }
                if (includeOpenHands && TryGetString(root, "action", out var action) && action == "finish"
                    && TryGetString(root, "source", out var source) && source == "agent")
                {
                    hasTerminalResponse = true;
                    terminalResponse = null;
                    if (root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object)
                    {
                        if (TryGetString(args, "final_thought", out var finalThought)) terminalResponse = finalThought;
                        else if (args.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Object && TryGetString(outputs, "content", out var content)) terminalResponse = content;
                    }
                }
                if (TryGetString(root, "type", out var eventType)
                    && string.Equals(eventType, "item.completed", StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object
                    && TryGetString(item, "type", out var itemType)
                    && string.Equals(itemType, "agent_message", StringComparison.OrdinalIgnoreCase)
                    && TryGetString(item, "text", out var text))
                {
                    lastMessage = text;
                }
            }
            catch (JsonException)
            {
            }
        }

        return hasTerminalResponse ? terminalResponse : lastMessage;
    }

    private static string? ExtractCheckpointFailure(string logs)
    {
        foreach (var line in logs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!TryGetString(root, "type", out var type)
                    || !string.Equals(type, "formicae.checkpoint", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var branch = TryGetString(root, "branch", out var parsedBranch) ? parsedBranch : "the workflow branch";
                var commit = TryGetString(root, "commitSha", out var parsedCommit) ? parsedCommit : null;
                var pushed = root.TryGetProperty("pushed", out var pushedProperty) && pushedProperty.ValueKind == JsonValueKind.True;
                return pushed && !string.IsNullOrWhiteSpace(commit)
                    ? $"Worker checkpointed commit {commit} on '{branch}' before the deadline. Retry the task to continue."
                    : $"Worker attempted a deadline checkpoint on '{branch}', but no checkpoint commit was pushed. Inspect the worker logs before retrying.";
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static ResolvedAiSettings ResolveSettingsFromOptions(OpenHandsOptions options)
        => new(
            AiSettings.DefaultId,
            AiSettings.DefaultName,
            TrimToNull(options.Provider),
            TrimToNull(options.DefaultModel),
            TrimToNull(options.EndpointUrl),
            AgentKinds.OpenHands,
            null,
            null,
            NormalizeAuthMethod(TrimToNull(options.AuthMethod) ?? OpenHandsAuthMethods.ApiKey),
            TrimToNull(options.LlmApiKeySecretName),
            null,
            "LLM_API_KEY",
            null,
            "auth.json",
            "/root/.codex",
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private static string ResolveModel(AgentTask task, ResolvedAiSettings settings, string authMethod)
    {
        if (!string.IsNullOrWhiteSpace(task.Model)) return task.Model;
        if (!string.IsNullOrWhiteSpace(settings.Model)) return settings.Model;
        return IsApiKeyAuth(authMethod) ? "openhands/claude-sonnet-4" : string.Empty;
    }

    private static Dictionary<string, string> BuildEnvironment(AgentTask task, string jobName, string model, ResolvedAiSettings settings, string authMethod, RuntimeJobOptions options, string? gitAccessToken)
    {
        var environment = new Dictionary<string, string>
        {
            ["FORMICAE_WORKFLOW_ID"] = task.WorkflowId.ToString("N"),
            ["FORMICAE_TASK_KIND"] = task.Kind.ToString(),
            ["FORMICAE_REPOSITORY_URL"] = task.RepositoryUrl,
            ["FORMICAE_BRANCH"] = task.BranchName,
            ["FORMICAE_TASK_PROMPT"] = task.Prompt,
            ["FORMICAE_OPENHANDS_AUTH_METHOD"] = authMethod,
            ["FORMICAE_MODEL"] = model,
            ["FORMICAE_EXTERNAL_ID"] = jobName,
            ["FORMICAE_AI_SETTINGS_ID"] = settings.Id,
            ["FORMICAE_CONTEXT_PATH"] = "/workspace/formicae/context"
        };

        if (task.ExecutionAttemptId is { } attemptId) environment["FORMICAE_EXECUTION_ATTEMPT_ID"] = attemptId.ToString("D");
        if (task.Kind == TaskRunKind.Custom && task.OutputSchema is { Count: > 0 })
            environment["FORMICAE_OUTPUT_SCHEMA"] = JsonSerializer.Serialize(task.OutputSchema, JsonSerializerOptions.Web);
        if (!string.IsNullOrWhiteSpace(gitAccessToken)) environment["FORMICAE_GIT_ACCESS_TOKEN"] = gitAccessToken;
        if (!string.IsNullOrWhiteSpace(options.WorkerCallbackUrl)) environment["FORMICAE_WORKER_CALLBACK_URL"] = options.WorkerCallbackUrl;
        if (!string.IsNullOrWhiteSpace(options.WorkerCallbackSecret)) environment["FORMICAE_WORKER_CALLBACK_SECRET"] = options.WorkerCallbackSecret;

        if (IsApiKeyAuth(authMethod))
        {
            environment["LLM_MODEL"] = model;
            if (!string.IsNullOrWhiteSpace(settings.EndpointUrl)) environment["LLM_BASE_URL"] = settings.EndpointUrl;
        }
        else if (IsAuthMethod(authMethod, OpenHandsAuthMethods.CodexSubscription))
        {
            environment["CODEX_HOME"] = "/tmp/codex-home";
            environment["FORMICAE_CODEX_AUTH_MOUNT_PATH"] = settings.SubscriptionCredentialMountPath ?? "/root/.codex";
            environment["FORMICAE_CODEX_AUTH_FILE_NAME"] = settings.SubscriptionCredentialFileName ?? "auth.json";
            if (!string.IsNullOrWhiteSpace(model)) environment["CODEX_CONFIG"] = $"{{\"model\":\"{model}\"}}";
        }

        if (string.Equals(settings.AgentKind, AgentKinds.Acp, StringComparison.OrdinalIgnoreCase))
        {
            environment["FORMICAE_AGENT_KIND"] = AgentKinds.Acp;
            if (!string.IsNullOrWhiteSpace(settings.AcpProvider)) environment["FORMICAE_ACP_PROVIDER"] = settings.AcpProvider;
            if (!string.IsNullOrWhiteSpace(settings.AcpCommand)) environment["FORMICAE_ACP_COMMAND"] = settings.AcpCommand;
        }

        return environment;
    }

    internal static IReadOnlyList<RuntimeJobSecretFile>? BuildSecretFiles(string jobName, ResolvedAiSettings settings, string authMethod, RuntimeJobOptions options)
    {
        var credentialJson = settings.CodexAuthJson ?? settings.SubscriptionCredentialJson;
        if (!IsAuthMethod(authMethod, OpenHandsAuthMethods.CodexSubscription) || string.IsNullOrWhiteSpace(credentialJson)) return null;
        var fileName = settings.SubscriptionCredentialFileName ?? options.CodexAuthSecretKey;
        var mountPath = settings.SubscriptionCredentialMountPath ?? options.CodexAuthMountPath;
        return [new RuntimeJobSecretFile($"{jobName}-codex-auth", mountPath, new Dictionary<string, string> { [fileName] = credentialJson })];
    }

    private static RuntimeJobSecretEnvironment? BuildSecretEnvironment(string jobName, ResolvedAiSettings settings, string authMethod)
    {
        if (!IsApiKeyAuth(authMethod) || string.IsNullOrWhiteSpace(settings.LlmApiKey) || string.IsNullOrWhiteSpace(settings.ApiKeyEnvironmentVariable))
        {
            return null;
        }

        return new RuntimeJobSecretEnvironment(
            $"{jobName}-api-auth",
            new Dictionary<string, string> { [settings.ApiKeyEnvironmentVariable] = settings.LlmApiKey });
    }
    private static string ResolveAuthMethod(string authMethod)
    {
        if (IsAuthMethod(authMethod, OpenHandsAuthMethods.CodexSubscription)) return OpenHandsAuthMethods.CodexSubscription;
        if (IsAuthMethod(authMethod, OpenHandsAuthMethods.ApiKey)) return OpenHandsAuthMethods.ApiKey;
        if (IsAuthMethod(authMethod, OpenHandsAuthMethods.OpenHandsCloud)) return OpenHandsAuthMethods.OpenHandsCloud;
        throw new InvalidOperationException($"Unsupported OpenHands auth method '{authMethod}'. Supported values are '{OpenHandsAuthMethods.ApiKey}', '{OpenHandsAuthMethods.OpenHandsCloud}' and '{OpenHandsAuthMethods.CodexSubscription}'.");
    }

    private static string ToRuntimeAuthMethod(string authMethod)
        => IsAuthMethod(authMethod, OpenHandsAuthMethods.CodexSubscription) ? RuntimeJobAuthMethods.CodexSubscription : RuntimeJobAuthMethods.ApiKey;

    private static bool IsApiKeyAuth(string authMethod)
        => IsAuthMethod(authMethod, OpenHandsAuthMethods.ApiKey) || IsAuthMethod(authMethod, OpenHandsAuthMethods.OpenHandsCloud);

    private static bool IsAuthMethod(string? actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAuthMethod(string authMethod)
    {
        if (IsAuthMethod(authMethod, OpenHandsAuthMethods.ApiKey)) return OpenHandsAuthMethods.ApiKey;
        if (IsAuthMethod(authMethod, OpenHandsAuthMethods.OpenHandsCloud)) return OpenHandsAuthMethods.OpenHandsCloud;
        if (IsAuthMethod(authMethod, OpenHandsAuthMethods.CodexSubscription)) return OpenHandsAuthMethods.CodexSubscription;
        return authMethod;
    }

    private static string? TrimToNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
