using System.Text.Json;
using hhnl.Formicae.Application.Integrations;

namespace hhnl.Formicae.Application.Workflows;

public sealed partial class WorkflowOrchestrator
{
    private async Task<bool> RunCreateBranchTaskAsync(Workflow workflow, WorkflowDefinitionStep step, CancellationToken token)
    {
        var run = await GetCurrentTaskRunAsync(workflow, token) ?? await CreateCurrentTaskRunAsync(workflow, TaskRunKind.CreateBranch, token);
        if (run.Status == TaskRunStatus.Succeeded)
        { await AdvanceDefinitionCursorAsync(workflow, "Branch created.", token); return true; }
        if (run.Status == TaskRunStatus.Failed)
        { await FailWorkflowAsync(workflow, run.FailureReason ?? "Branch creation failed.", null, token); return true; }
        try
        {
            var factory = devOpsPlatforms ?? throw new InvalidOperationException("DevOps integration is unavailable.");
            PreparedCreateBranchExecution prepared;
            DevOpsPlatformContext context;
            if (run.CustomTaskExecutionJson is null)
            {
                var settings = step.CreateBranch ?? throw new InvalidOperationException("Create branch settings are missing.");
                var errors = CreateBranchDefinitions.ValidateStep(step);
                if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors.Select(error => error.Message)));
                var document = await ResolveDefinitionAsync(workflow, token);
                var provenance = await ResolveInputProvenanceAsync(workflow, run, step, document, token);
                var values = (settings.Inputs ?? new Dictionary<string, JsonElement>()).ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
                foreach (var (name, source) in provenance)
                    values[name] = source.Value?.Clone() ?? throw new InvalidOperationException($"Bound input '{name}' is missing.");
                foreach (var (name, value) in values)
                    if (!CreateBranchDefinitions.ValidValue(name, value)) throw new InvalidOperationException($"Invalid branch input '{name}'.");
                var sourceBranch = values.TryGetValue("sourceBranch", out var sourceValue) ? sourceValue.GetString()! : settings.SourceBranch;
                var targetBranch = values.TryGetValue("targetBranch", out var targetValue) ? targetValue.GetString()! : settings.BranchName;
                if (sourceBranch == targetBranch) throw new InvalidOperationException("The new branch must differ from the source branch.");
                var repositories = await (integrations ?? throw new InvalidOperationException("Integration store is unavailable.")).ListAllRepositoriesAsync(token);
                var repository = repositories.SingleOrDefault(item => values.TryGetValue("repository", out var repositoryValue) ? item.RepositoryUrl == repositoryValue.GetString() : item.Id == settings.RepositoryId)
                    ?? throw new InvalidOperationException("The selected repository is no longer connected.");
                context = await factory.CreateForRepositoryAsync(repository.RepositoryUrl, token);
                RequireGitHub(context);
                if ((await context.Platform.ListBranchesAsync(context.Repository, token)).Contains(targetBranch, StringComparer.Ordinal))
                    throw new InvalidOperationException($"Destination branch '{targetBranch}' already exists.");
                var sha = await context.Platform.GetBranchHeadShaAsync(context.Repository, sourceBranch, token);
                prepared = new(repository.RepositoryUrl, sourceBranch, targetBranch, sha, provenance.Count == 0 ? null : provenance);
                run.CustomTaskExecutionJson = JsonSerializer.Serialize(prepared, CustomExecutionJsonOptions);
            }
            else
            {
                prepared = JsonSerializer.Deserialize<PreparedCreateBranchExecution>(run.CustomTaskExecutionJson, CustomExecutionJsonOptions)
                    ?? throw new InvalidOperationException("Stored branch inputs are missing.");
                context = await factory.CreateForRepositoryAsync(prepared.RepositoryUrl, token);
                RequireGitHub(context);
            }
            WorkflowVariableDefinitions.ValidatePinnedEvidence(prepared.Provenance?.Values ?? [], await ResolveDefinitionAsync(workflow, token), run.LoopIteration);
            // Persist the resolved source before mutation. A retry can recognize a completed creation
            // after the process stopped between GitHub's response and recording task success.
            await StartTaskRunAsync(workflow, run, token);
            if ((await context.Platform.ListBranchesAsync(context.Repository, token)).Contains(prepared.BranchName, StringComparer.Ordinal))
            {
                var existingSha = await context.Platform.GetBranchHeadShaAsync(context.Repository, prepared.BranchName, token);
                if (existingSha != prepared.SourceSha)
                    throw new InvalidOperationException($"Destination branch '{prepared.BranchName}' exists at a different commit.");
            }
            else await context.Platform.CreateBranchAsync(context.Repository, null, prepared.SourceSha, prepared.BranchName, token);
            await CompleteTaskRunAsync(workflow, run,
                $"Created branch '{prepared.BranchName}' from '{prepared.SourceBranch}' at {prepared.SourceSha} in {prepared.RepositoryUrl}.", true, null, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            await CompleteTaskRunAsync(workflow, run, "", false, exception.Message, token);
            await FailWorkflowAsync(workflow, exception.Message, null, token);
            return true;
        }
        await AdvanceDefinitionCursorAsync(workflow, "Branch created.", token);
        return true;
    }

    private static void RequireGitHub(DevOpsPlatformContext context)
    {
        if (context.Repository.ProviderType != DevOpsProviderType.GitHub)
            throw new InvalidOperationException("Create branch requires a connected GitHub repository.");
    }
}
