using System.Text.Json;
using hhnl.Formicae.Application.Integrations;

namespace hhnl.Formicae.Application.Workflows;

public sealed partial class WorkflowOrchestrator
{
    private async Task<bool> RunIssueLabelsTaskAsync(Workflow workflow, WorkflowDefinitionStep step, CancellationToken token)
    {
        var run = await GetCurrentTaskRunAsync(workflow, token) ?? await CreateCurrentTaskRunAsync(workflow, TaskRunKind.AddIssueLabels, token);
        if (run.Status == TaskRunStatus.Succeeded)
        { await AdvanceDefinitionCursorAsync(workflow, "Issue labels added.", token); return true; }
        if (run.Status == TaskRunStatus.Failed)
        { await FailWorkflowAsync(workflow, run.FailureReason ?? "Issue labels failed.", null, token); return true; }
        try
        {
            PreparedIssueLabelsExecution prepared;
            if (run.CustomTaskExecutionJson is null)
            {
                var settings = step.IssueLabels ?? throw new InvalidOperationException("Issue labels settings are missing.");
                var document = await ResolveDefinitionAsync(workflow, token);
                var provenance = await ResolveInputProvenanceAsync(workflow, run, step, document, token);
                var values = (settings.Inputs ?? new Dictionary<string, JsonElement>()).ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
                foreach (var (name, source) in provenance)
                    values[name] = source.Value?.Clone() ?? throw new InvalidOperationException($"Bound input '{name}' is missing.");
                prepared = new(values, provenance);
                run.CustomTaskExecutionJson = JsonSerializer.Serialize(prepared, CustomExecutionJsonOptions);
            }
            else prepared = JsonSerializer.Deserialize<PreparedIssueLabelsExecution>(run.CustomTaskExecutionJson, CustomExecutionJsonOptions)
                ?? throw new InvalidOperationException("Stored issue labels inputs are missing.");
            WorkflowVariableDefinitions.ValidatePinnedEvidence(prepared.Provenance.Values, await ResolveDefinitionAsync(workflow, token), run.LoopIteration);
            foreach (var input in IssueLabelsDefinitions.Inputs)
                if (!prepared.Inputs.TryGetValue(input.Name, out var value) || !IssueLabelsDefinitions.ValidValue(input.Name, value))
                    throw new InvalidOperationException($"Invalid labels input '{input.Name}'.");
            var platform = await (devOpsPlatforms ?? throw new InvalidOperationException("DevOps integration is unavailable."))
                .CreateForRepositoryAsync(workflow.RepositoryUrl, token);
            if (platform.Repository.ProviderType != DevOpsProviderType.GitHub)
                throw new InvalidOperationException("Add issue labels requires a connected GitHub repository.");
            run.ExecutionAttemptId ??= Guid.NewGuid();
            await StartTaskRunAsync(workflow, run, token);
            var issue = DevOpsReferenceParser.ParseIssueUrl(DevOpsProviderType.GitHub,
                $"{platform.Repository.RepositoryUrl}/issues/{prepared.Inputs["issueId"].GetInt32()}");
            await platform.Platform.AddIssueLabelsAsync(issue, IssueLabelsDefinitions.ParseLabels(prepared.Inputs["labels"].GetString()!), token);
            await CompleteTaskRunAsync(workflow, run, "Issue labels added.", true, null, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            await CompleteTaskRunAsync(workflow, run, "", false, exception.Message, token);
            await FailWorkflowAsync(workflow, exception.Message, null, token);
            return true;
        }
        await AdvanceDefinitionCursorAsync(workflow, "Issue labels added.", token);
        return true;
    }
}
