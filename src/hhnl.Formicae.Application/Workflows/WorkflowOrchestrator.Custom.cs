using System.Text;
using System.Text.Json;

namespace hhnl.Formicae.Application.Workflows;

public sealed partial class WorkflowOrchestrator
{
    private static readonly JsonSerializerOptions CustomExecutionJsonOptions = new(JsonSerializerDefaults.Web);
    private const int CustomOutputLimit = 262144;

    private async Task<bool> RunCustomTaskAsync(Workflow workflow, WorkflowDefinitionStep step, CancellationToken token)
    {
        var run = await GetCurrentTaskRunAsync(workflow, token)
            ?? await CreateCurrentTaskRunAsync(workflow, TaskRunKind.Custom, token);
        try
        {
            if (run.Status == TaskRunStatus.Succeeded)
            {
                await AdvanceDefinitionCursorAsync(workflow, "Custom task completed.", token);
                return true;
            }
            if (run.Status == TaskRunStatus.Failed)
            {
                await FailWorkflowAsync(workflow, run.FailureReason ?? "Custom task failed.", null, token);
                return true;
            }
            if (run.Status == TaskRunStatus.Running && !string.IsNullOrWhiteSpace(run.ExternalId))
            {
                var result = await agentRunner.TryGetResultAsync(run.ExternalId, token);
                if (result is null) return false;
                await CompleteCustomTaskAsync(workflow, run, result, token);
                return true;
            }

            PreparedAgentTask prepared;
            try
            {
                var settings = step.CustomTask ?? throw new InvalidOperationException("Custom task settings are missing.");
                PreparedCustomTaskExecution execution;
                if (run.CustomTaskExecutionJson is null)
                {
                    var document = await ResolveDefinitionAsync(workflow, token);
                    var provenance = new Dictionary<string, CustomTaskInputProvenance>(StringComparer.Ordinal);
                    foreach (var (name, binding) in settings.Bindings ?? new Dictionary<string, CustomTaskInputBinding>())
                    {
                        var producer = document.Steps.Single(item => item.Id == binding.StepId);
                        var inLoop = document.Loops?.Any(loop => loop.BodyStepIds.Contains(producer.Id)) == true;
                        var source = await store.GetTaskRunExecutionAsync(workflow.Id, producer.Id, inLoop ? run.LoopIteration : null, token);
                        if (source is not { Status: TaskRunStatus.Succeeded, StructuredOutputsJson: not null, ExecutionAttemptId: not null })
                            throw new InvalidOperationException($"Bound input '{name}' requires successful validated outputs from '{producer.Id}'.");
                        var outputs = CustomTaskDefinitions.ParseOutputs(source.StructuredOutputsJson, CustomTaskDefinitions.OutputSchemaFor(producer));
                        provenance[name] = new(producer.Id, binding.OutputName, source.Id, source.ExecutionAttemptId.Value, source.LoopIteration,
                            outputs.TryGetValue(binding.OutputName, out var value) ? value : null);
                    }
                    execution = CustomTaskDefinitions.Prepare(settings, workflow, provenance);
                    run.CustomTaskExecutionJson = JsonSerializer.Serialize(execution, CustomExecutionJsonOptions);
                }
                else
                {
                    execution = JsonSerializer.Deserialize<PreparedCustomTaskExecution>(run.CustomTaskExecutionJson, CustomExecutionJsonOptions)
                        ?? throw new InvalidOperationException("Stored custom task execution is empty.");
                    CustomTaskDefinitions.ValidatePrepared(execution, settings);
                    var document = await ResolveDefinitionAsync(workflow, token);
                    foreach (var source in execution.Provenance?.Values ?? [])
                    {
                        var producerLoop = document.Loops?.FirstOrDefault(loop => loop.BodyStepIds.Contains(source.StepId));
                        if (source.LoopIteration != (producerLoop is null ? null : run.LoopIteration))
                            throw new InvalidOperationException("Prepared binding provenance does not match this loop iteration.");
                    }
                }
                run.ExecutionAttemptId ??= Guid.NewGuid();
                var context = JsonSerializer.Serialize(new { execution.Inputs, execution.WorkflowFields, Outputs = settings.Snapshot!.Outputs }, CustomExecutionJsonOptions);
                prepared = await PrepareAgentTaskAsync(workflow, run, new AgentTask(workflow.Id, TaskRunKind.Custom,
                    execution.Prompt, workflow.RepositoryUrl, workflow.BaseBranch, workflow.Model,
                    [new AgentTaskContextFile("custom-task-inputs.json", context)],
                    ExecutionAttemptId: run.ExecutionAttemptId, TimeoutSeconds: execution.TimeoutSeconds), token);
                if (string.IsNullOrWhiteSpace(prepared.Task.Prompt) || Encoding.UTF8.GetByteCount(prepared.Task.Prompt) > 131072)
                    throw new InvalidOperationException("The composed custom task prompt must be nonblank and no larger than 131072 UTF-8 bytes.");
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException or ArgumentException)
            {
                await CompleteTaskRunAsync(workflow, run, "", false, exception.Message, token);
                await FailWorkflowAsync(workflow, exception.Message, null, token);
                return true;
            }

            // This write owns the prepared context and attempt before any external work can start.
            await StartTaskRunAsync(workflow, run, token);
            if (workflow.Status != WorkflowStatus.Running)
                await TransitionWorkflowAsync(workflow, WorkflowStatus.Running, WorkflowStep.Custom, "Custom task started.", token);

            AgentRunStartResult started;
            try { started = await agentRunner.StartAsync(prepared.Task, token); }
            catch (Exception exception) when (IsUncertainParallelTransport(exception, token))
            {
                await AddCustomWarningAsync(workflow, run, exception, token);
                return false;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
            {
                await CompleteTaskRunAsync(workflow, run, "", false, exception.Message, token);
                await FailWorkflowAsync(workflow, exception.Message, null, token);
                return true;
            }
            // Once accepted, bookkeeping failures must keep this attempt available for polling/reattachment.
            if (started.CompletedResult is not null)
                await CompleteCustomTaskAsync(workflow, run, started.CompletedResult, token);
            else
                await AssignExternalJobAsync(workflow, run, started.ExternalId, token);
            await AddEventAsync(workflow.Id, run.Id, "AgentSettingsResolved", "Information", "Custom task agent settings resolved.",
                new { aiSettingsId = started.AiSettingsId ?? prepared.Task.AiSettingsId ?? AiSettings.DefaultId,
                    model = started.Model ?? prepared.Task.Model, personaId = prepared.Persona?.Id ?? "default",
                    personaRevision = prepared.Persona?.Revision ?? 1, personaName = prepared.Persona?.Name ?? "Default behavior",
                    prepared.Task.TimeoutSeconds, externalId = started.ExternalId, executionAttemptId = run.ExecutionAttemptId,
                    environment = EnvironmentAudit(prepared.Task.OriginalEnvironmentSnapshot ?? prepared.Task.EnvironmentSnapshot, prepared.Task.Capabilities), imageSelection = prepared.Task.ImageSelection, imageSnapshot = prepared.Task.ImageSnapshot, effectiveImage = prepared.Task.EnvironmentSnapshot?.Configuration.Image, capabilities = prepared.Task.Capabilities, secretReferences = prepared.Task.SecretReferences }, token);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            await AddCustomWarningAsync(workflow, run, exception, token);
            return false;
        }
    }

    private async Task CompleteCustomTaskAsync(Workflow workflow, TaskRun run, AgentRunResult result, CancellationToken token)
    {
        result = await ValidateCustomTaskResultAsync(workflow, run, result, token);
        await CompleteTaskRunAsync(workflow, run, result, token);
        await AddAgentOutputLogAsync(workflow.Id, run, result, token);
        if (result.Succeeded) await AdvanceDefinitionCursorAsync(workflow, "Custom task completed.", token);
        else await FailWorkflowAsync(workflow, result.FailureReason ?? "Custom task failed.", BuildFailureDetails(run, result), token);
    }

    private async Task<AgentRunResult> ValidateCustomTaskResultAsync(Workflow workflow, TaskRun run, AgentRunResult result, CancellationToken token)
    {
        if (result.Output.Length > CustomOutputLimit)
        {
            const string marker = "\n[Output truncated: custom task output limit exceeded]";
            result = result with { Succeeded = false, Output = result.Output[..(CustomOutputLimit - marker.Length)] + marker,
                FailureReason = "Custom task output exceeded the 262144 character limit." };
        }
        run.StructuredOutputsJson = null;
        if (result.Succeeded)
        {
            try
            {
                var document = await ResolveDefinitionAsync(workflow, token);
                var schema = document.Steps.Single(step => step.Id == run.DefinitionStepId).CustomTask!.Snapshot!.Outputs;
                if (schema.Count > 0 && !result.OutputIsFinalResponse)
                    throw new InvalidOperationException("Custom task declared outputs but the CLI did not emit an authoritative final response.");
                if (schema.Count > 0)
                    run.StructuredOutputsJson = JsonSerializer.Serialize(CustomTaskDefinitions.ParseOutputs(result.Output, schema), CustomExecutionJsonOptions);
            }
            catch (InvalidOperationException exception)
            { result = result with { Succeeded = false, FailureReason = exception.Message }; }
        }
        return result;
    }

    private async Task AddCustomWarningAsync(Workflow workflow, TaskRun run, Exception exception, CancellationToken token)
    {
        try
        {
            await store.AddLogAsync(new WorkflowLog { WorkflowId = workflow.Id, TaskRunId = run.Id, Level = "Warning",
                ExecutionAttemptId = run.ExecutionAttemptId, ExternalId = run.ExternalId,
                Message = $"{run.Kind} task '{run.DefinitionStepId}' will resume its existing attempt after an orchestration error: {exception.Message}",
                CreatedAt = clock.UtcNow }, token);
        }
        catch (Exception) when (!token.IsCancellationRequested) { }
    }
}
