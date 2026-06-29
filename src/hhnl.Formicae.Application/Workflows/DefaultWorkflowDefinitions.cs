using System.Text.Json;

namespace hhnl.Formicae.Application.Workflows;

public static class DefaultWorkflowDefinitions
{
    public static readonly Guid MvpDefinitionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid MvpVersionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public const string MvpName = "MVP GitHub issue workflow";
    public const string V1Alpha1Schema = "formicae.workflow/v1alpha1";

    public static (WorkflowDefinition Definition, WorkflowDefinitionVersion Version) CreateMvp(DateTimeOffset? createdAt = null)
    {
        var now = createdAt ?? DateTimeOffset.UtcNow;
        var document = CreateMvpDocument();
        var definition = new WorkflowDefinition
        {
            Id = MvpDefinitionId,
            Name = MvpName,
            CreatedAt = now,
            UpdatedAt = now
        };
        var version = new WorkflowDefinitionVersion
        {
            Id = MvpVersionId,
            WorkflowDefinitionId = definition.Id,
            Version = 1,
            DslSchemaVersion = document.Schema,
            IsEnabled = true,
            IsDefault = true,
            DefinitionJson = WorkflowDefinitionJson.Serialize(document),
            CreatedAt = now
        };

        return (definition, version);
    }

    public static WorkflowDefinitionDocument CreateMvpDocument()
        => new(
            V1Alpha1Schema,
            "plan",
            [
                new WorkflowDefinitionStep("plan", "builtins.plan", "implement", "Plan"),
                new WorkflowDefinitionStep("implement", "builtins.implement", "createPullRequest", "Implement"),
                new WorkflowDefinitionStep("createPullRequest", "builtins.create-pull-request", "addressComments", "Create pull request"),
                new WorkflowDefinitionStep("addressComments", "builtins.address-comments", null, "Address comments")
            ]);
}

public static class WorkflowDefinitionJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(WorkflowDefinitionDocument document)
        => JsonSerializer.Serialize(document, Options);

    public static WorkflowDefinitionDocument? Deserialize(string json)
        => JsonSerializer.Deserialize<WorkflowDefinitionDocument>(json, Options);
}
