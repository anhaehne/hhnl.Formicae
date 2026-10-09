using System.Text.Json;
using hhnl.Formicae.Application.Integrations;

namespace hhnl.Formicae.Application.Workflows;

public sealed partial class WorkflowOrchestrator
{
    private async Task<Dictionary<string, CustomTaskInputProvenance>> ResolveInputProvenanceAsync(
        Workflow workflow, TaskRun run, WorkflowDefinitionStep step, WorkflowDefinitionDocument document, CancellationToken token)
    {
        var provenance = new Dictionary<string, CustomTaskInputProvenance>(StringComparer.Ordinal);
        async Task<CustomTaskInputProvenance> Load(CustomTaskInputBinding binding)
        {
            var producer = document.Steps.FirstOrDefault(item => item.Id == binding.StepId)
                ?? (document.Triggers?.Any(evt => evt.Id == binding.StepId && evt.Type == WorkflowTriggerType.DevOpsIssueCreated) == true
                    ? new WorkflowDefinitionStep(binding.StepId, "github.issue-created")
                    : throw new InvalidOperationException($"Producer '{binding.StepId}' is missing from the pinned definition."));
            var inLoop = document.Loops?.Any(loop => loop.BodyStepIds.Contains(producer.Id)) == true;
            var source = await store.GetTaskRunExecutionAsync(workflow.Id, producer.Id, inLoop ? run.LoopIteration : null, token);
            if (source is not { Status: TaskRunStatus.Succeeded, StructuredOutputsJson: not null, ExecutionAttemptId: not null })
                throw new InvalidOperationException($"Bound input requires successful validated outputs from '{producer.Id}'.");
            var outputs = CustomTaskDefinitions.ParseProducerOutputs(producer, source.StructuredOutputsJson);
            return new(producer.Id, binding.OutputName, source.Id, source.ExecutionAttemptId.Value, source.LoopIteration,
                outputs.TryGetValue(binding.OutputName, out var value) ? value : null);
        }
        foreach (var (name, binding) in CustomTaskDefinitions.BindingsFor(step))
            provenance[name] = await WorkflowVariableDefinitions.ResolveAsync(document, binding, Load);
        return provenance;
    }

    private async Task<bool> RunIssueCommentTaskAsync(Workflow workflow, WorkflowDefinitionStep step, CancellationToken token)
    {
        var run = await GetCurrentTaskRunAsync(workflow, token) ?? await CreateCurrentTaskRunAsync(workflow, TaskRunKind.AddIssueComment, token);
        if (run.Status == TaskRunStatus.Succeeded)
        { await AdvanceDefinitionCursorAsync(workflow, "Issue comment added.", token); return true; }
        if (run.Status == TaskRunStatus.Failed)
        { await FailWorkflowAsync(workflow, run.FailureReason ?? "Issue comment failed.", null, token); return true; }
        try
        {
            PreparedIssueCommentExecution prepared;
            if (run.CustomTaskExecutionJson is null)
            {
                var settings = step.IssueComment ?? throw new InvalidOperationException("Issue comment settings are missing.");
                var document = await ResolveDefinitionAsync(workflow, token);
                var provenance = await ResolveInputProvenanceAsync(workflow, run, step, document, token);
                var values = (settings.Inputs ?? new Dictionary<string, JsonElement>()).ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
                foreach (var (name, source) in provenance)
                    values[name] = source.Value?.Clone() ?? throw new InvalidOperationException($"Bound input '{name}' is missing.");
                prepared = new(values, provenance);
                run.CustomTaskExecutionJson = JsonSerializer.Serialize(prepared, CustomExecutionJsonOptions);
            }
            else prepared = JsonSerializer.Deserialize<PreparedIssueCommentExecution>(run.CustomTaskExecutionJson, CustomExecutionJsonOptions)
                ?? throw new InvalidOperationException("Stored issue comment inputs are missing.");
            WorkflowVariableDefinitions.ValidatePinnedEvidence(prepared.Provenance.Values, await ResolveDefinitionAsync(workflow, token), run.LoopIteration);
            foreach (var input in IssueCommentDefinitions.Inputs)
                if (!prepared.Inputs.TryGetValue(input.Name, out var value) || !IssueCommentDefinitions.ValidValue(input.Name, value))
                    throw new InvalidOperationException($"Invalid comment input '{input.Name}'.");
            var platform = await (devOpsPlatforms ?? throw new InvalidOperationException("DevOps integration is unavailable."))
                .CreateForRepositoryAsync(workflow.RepositoryUrl, token);
            if (platform.Repository.ProviderType != DevOpsProviderType.GitHub)
                throw new InvalidOperationException("Add issue comment requires a connected GitHub repository.");
            run.ExecutionAttemptId ??= Guid.NewGuid();
            await StartTaskRunAsync(workflow, run, token);
            var issue = DevOpsReferenceParser.ParseIssueUrl(DevOpsProviderType.GitHub,
                $"{platform.Repository.RepositoryUrl}/issues/{prepared.Inputs["issueId"].GetInt32()}");
            await platform.Platform.CreateIssueCommentAsync(issue, prepared.Inputs["text"].GetString()!, token);
            await CompleteTaskRunAsync(workflow, run, "Issue comment added.", true, null, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            await CompleteTaskRunAsync(workflow, run, "", false, exception.Message, token);
            await FailWorkflowAsync(workflow, exception.Message, null, token);
            return true;
        }
        await AdvanceDefinitionCursorAsync(workflow, "Issue comment added.", token);
        return true;
    }
}
