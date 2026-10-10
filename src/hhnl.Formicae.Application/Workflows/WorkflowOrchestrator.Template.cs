using System.Text.Json;

namespace hhnl.Formicae.Application.Workflows;

public sealed partial class WorkflowOrchestrator
{
    private async Task<bool> RunTemplateTaskAsync(Workflow workflow, WorkflowDefinitionStep step, CancellationToken token)
    {
        var run = await GetCurrentTaskRunAsync(workflow, token) ?? await CreateCurrentTaskRunAsync(workflow, TaskRunKind.Template, token);
        if (run.Status == TaskRunStatus.Succeeded)
        { await AdvanceDefinitionCursorAsync(workflow, "Template rendered.", token); return true; }
        if (run.Status == TaskRunStatus.Failed)
        { await FailWorkflowAsync(workflow, run.FailureReason ?? "Template failed.", null, token); return true; }
        try
        {
            var settings = step.Template ?? throw new InvalidOperationException("Template settings are missing.");
            var document = await ResolveDefinitionAsync(workflow, token);
            PreparedTemplateExecution prepared;
            if (run.CustomTaskExecutionJson is null)
            {
                var provenance = await ResolveInputProvenanceAsync(workflow, run, step, document, token);
                var values = provenance.ToDictionary(pair => pair.Key, pair => pair.Value.Value?.Clone()
                    ?? throw new InvalidOperationException($"Template input '{pair.Key}' is unavailable."));
                prepared = new(values, provenance);
                run.CustomTaskExecutionJson = JsonSerializer.Serialize(prepared, CustomExecutionJsonOptions);
            }
            else prepared = JsonSerializer.Deserialize<PreparedTemplateExecution>(run.CustomTaskExecutionJson, CustomExecutionJsonOptions)
                ?? throw new InvalidOperationException("Stored template inputs are missing.");
            WorkflowVariableDefinitions.ValidatePinnedEvidence(prepared.Provenance.Values, document, run.LoopIteration);
            run.ExecutionAttemptId ??= Guid.NewGuid();
            await StartTaskRunAsync(workflow, run, token);
            var text = WorkflowTemplateDefinitions.Render(settings, prepared.Inputs, token);
            run.StructuredOutputsJson = JsonSerializer.Serialize(new { text }, CustomExecutionJsonOptions);
            _ = CustomTaskDefinitions.ParseOutputs(run.StructuredOutputsJson, WorkflowTemplateDefinitions.Outputs);
            await CompleteTaskRunAsync(workflow, run, text, true, null, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            run.StructuredOutputsJson = null;
            await CompleteTaskRunAsync(workflow, run, "", false, exception.Message, token);
            await FailWorkflowAsync(workflow, exception.Message, null, token);
            return true;
        }
        await AdvanceDefinitionCursorAsync(workflow, "Template rendered.", token);
        return true;
    }
}
