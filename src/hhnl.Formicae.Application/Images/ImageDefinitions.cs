using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Application.Images;

public sealed record PreparedImagePolicy(IReadOnlyList<string> PullSecretNames);

public static class ImageDefinitions
{
    public static async Task<EnvironmentConfiguration> ResolveConfigurationAsync(EnvironmentConfiguration configuration,
        ImageService? images, PreparedImagePolicy? policy, CancellationToken token)
    {
        var selection=configuration.ImageSelection;
        if(selection is null) {
            if(configuration.ImageSnapshot is not null) throw new ArgumentException("Prepared image snapshot requires an image selection.");
            return configuration;
        }
        if(selection.Mode == "managed") {
            var snapshot=images is null || selection.ImageId is null || selection.BuildId is null ? null : await images.SnapshotAsync(selection.ImageId,selection.BuildId,policy?.PullSecretNames??[],token);
            if(snapshot is null) throw new ArgumentException("Select an available Ready image build.");
            return configuration with { ImageSnapshot=snapshot, Image=Settings(snapshot) };
        }
        if(selection.Mode != "platform" || selection.ImageId is not null || selection.BuildId is not null) throw new ArgumentException("Environment image selection must be managed or platform.");
        return configuration with { Image=null,ImageSnapshot=null };
    }
    public static async Task<EnvironmentDefinitionResolution> ResolveAsync(WorkflowDefinitionDocument document, ImageService? images,
        PreparedImagePolicy? policy, CancellationToken token)
    {
        var errors=new List<WorkflowDefinitionValidationError>();
        if(document.Steps is null || document.Steps.Any(x=>x is null))return new(document,new([]));
        var steps=new List<WorkflowDefinitionStep>();
        foreach(var step in document.Steps)
        {
            var resolved=step with { ImageSnapshot=null };
            var selection=step.ImageSelection;
            if(selection is null) {
                if(step.ImageSnapshot is not null) errors.Add(Error("An image snapshot requires a managed image selection.",step.Id));
            }
            else if(selection.Mode=="managed") {
                var snapshot=images is null || selection.ImageId is null || selection.BuildId is null?null:await images.SnapshotAsync(selection.ImageId,selection.BuildId,policy?.PullSecretNames??[],token);
                if(snapshot is null) errors.Add(Error("Select an available Ready image build.",step.Id));
                resolved=resolved with { ImageSnapshot=snapshot };
            }
            steps.Add(resolved);
        }
        var result=document with { Steps=steps };
        return new(result,new([.. errors,.. ValidateRuntime(result).Errors]));
    }
    public static WorkflowDefinitionValidationResult ValidateRuntime(WorkflowDefinitionDocument document)
    {
        var errors=new List<WorkflowDefinitionValidationError>();
        foreach(var step in document.Steps??[])
        {
            if(step is null)continue;
            if((step.ImageSelection is not null || step.ImageSnapshot is not null) && !WorkflowExecutionExtensions.IsExecutionTask(step.Uses))errors.Add(Error("Only execution tasks can select an image.",step.Id));
            var selection=step.ImageSelection;
            if(selection is null || selection.Mode is "inherit" or "platform") {
                if(step.ImageSnapshot is not null || selection?.ImageId is not null || selection?.BuildId is not null) errors.Add(Error("Inherited/platform images cannot contain managed build fields.",step.Id));
            }
            else if(selection.Mode!="managed" || !ValidSnapshot(selection,step.ImageSnapshot)) errors.Add(Error("Managed image selection requires a matching pinned compatible build snapshot.",step.Id));
        }
        return new(errors);
    }
    public static bool ValidSnapshot(ImageSelection selection,PreparedImageSnapshot? snapshot)
        => snapshot is not null && snapshot.ImageId==selection.ImageId && snapshot.BuildId==selection.BuildId &&
           !string.IsNullOrWhiteSpace(snapshot.ImageId) && !string.IsNullOrWhiteSpace(snapshot.BuildId) && snapshot.SourceRevision>0 &&
           !string.IsNullOrWhiteSpace(snapshot.Name) && snapshot.Name.Length<=120 && snapshot.WorkerProtocolVersion==1 && snapshot.Platform=="linux/amd64" &&
           ImageService.IsDigest(snapshot.Reference) && snapshot.PullPolicy=="IfNotPresent" &&
           snapshot.PullSecretNames is not null && snapshot.PullSecretNames.Count<=16 && snapshot.PullSecretNames.All(WorkflowExecutionExtensions.ValidSecretName);
    public static EnvironmentImageSettings Settings(PreparedImageSnapshot snapshot)=>new(snapshot.Reference,snapshot.PullPolicy,snapshot.PullSecretNames);
    public static EnvironmentSnapshot? EffectiveEnvironment(WorkflowDefinitionDocument document,WorkflowDefinitionStep step)
    {
        var environment=EnvironmentDefinitions.ResolveForTask(document,step);
        if(environment is null || step.ImageSelection is null || step.ImageSelection.Mode=="inherit") return environment;
        var validation=ValidateRuntime(document);if(!validation.IsValid)throw new InvalidOperationException(string.Join(" ",validation.Errors.Select(x=>x.Message)));
        return environment with {Configuration=environment.Configuration with {Image=step.ImageSelection.Mode=="platform"?null:Settings(step.ImageSnapshot!),ImageSelection=null,ImageSnapshot=null}};
    }
    private static WorkflowDefinitionValidationError Error(string message,string node)=>new("definition.image.invalid",message,"steps[].imageSelection",node);
}
