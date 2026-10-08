using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using hhnl.Formicae.Application.Images;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Images;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace hhnl.Formicae.Tests;

public sealed class ManagedImageTests
{
    private static readonly CancellationToken Token=default;
    private static string Digest=>"registry.example/formicae/local/test@sha256:"+new string('a',64);
    private static ImageRequest Request(string dockerfile="FROM worker\nRUN echo tools")=>new("Tools","Description",new(dockerfile));
    private static (InMemoryImageStore Store,ImageService Service) Services()
    {var store=new InMemoryImageStore();return(store,new(store,new FakeImageSourceResolver(),new SystemClock()));}
    [Fact]
    public async Task Edits_and_rebuilds_preserve_immutable_sources_and_ready_digest()
    {
        var (store,service)=Services();var image=await service.CreateAsync(Request(),Token);var first=await service.QueueAsync(image.Id,1,Token);
        var build=(await store.BuildAsync(first!.Id,Token))!;
        Assert.True(await store.UpdateBuildAsync(build with { State=ImageBuildState.Ready,Reference=Digest,Revision=2 },1,Token));
        var old=await service.SnapshotAsync(image.Id,build.Id,["pull"],Token);
        await service.UpdateAsync(image.Id,Request("FROM worker\nRUN echo changed") with { ExpectedRevision=1 },Token);
        var second=await service.QueueAsync(image.Id,2,Token);
        Assert.NotEqual(first.Id,second!.Id);Assert.Equal(1,(await store.BuildAsync(first.Id,Token))!.SourceRevision);
        Assert.Contains("echo tools",(await store.BuildAsync(first.Id,Token))!.SourceJson);
        var retained=await service.SnapshotAsync(image.Id,first.Id,["pull"],Token);Assert.Equal(old!.Reference,retained!.Reference);Assert.Equal(old.SourceRevision,retained.SourceRevision);Assert.Equal(old.PullSecretNames,retained.PullSecretNames);
        Assert.Equal(2,(await service.RevisionsAsync(image.Id,Token)).Count);
        await Assert.ThrowsAsync<ImageConflictException>(()=>service.UpdateAsync(image.Id,Request() with {ExpectedRevision=1},Token));
    }
    [Fact]
    public async Task Archive_disables_new_selection_and_preserves_saved_artifact()
    {
        var (store,service)=Services();var image=await service.CreateAsync(Request(),Token);var queued=await service.QueueAsync(image.Id,1,Token);var build=(await store.BuildAsync(queued!.Id,Token))!;
        await store.UpdateBuildAsync(build with {State=ImageBuildState.Ready,Reference=Digest,Revision=2},1,Token);
        var saved=await service.SnapshotAsync(image.Id,build.Id,[],Token);
        Assert.True(await service.ArchiveAsync(image.Id,1,Token));Assert.Empty(await service.ListAsync(Token));Assert.Null(await service.SnapshotAsync(image.Id,build.Id,[],Token));
        Assert.Equal(Digest,saved!.Reference);Assert.Equal(Digest,(await store.BuildAsync(build.Id,Token))!.Reference);
    }
    [Fact]
    public async Task Cancellation_wins_over_stale_success_and_triggers_cleanup()
    {
        var (store,service)=Services();var image=await service.CreateAsync(Request(),Token);var build=await service.QueueAsync(image.Id,1,Token);
        var runtime=new RacingRuntime(()=>service.CancelAsync(image.Id,build!.Id,Token));
        await new ImageBuildCoordinator(store,runtime,new SystemClock()).TickAsync(2,TimeSpan.FromMinutes(20),Token);
        Assert.Equal(ImageBuildState.Cancelled,(await store.BuildAsync(build!.Id,Token))!.State);Assert.True(runtime.Cleaned);
    }
    [Fact]
    public async Task Ready_requires_a_verified_digest_and_successful_probe()
    {
        var (store,service)=Services();var image=await service.CreateAsync(Request(),Token);var build=await service.QueueAsync(image.Id,1,Token);
        var runtime=new ObservationRuntime(new(ImageBuildState.Ready,Reference:"registry.example/tools:latest"));
        await new ImageBuildCoordinator(store,runtime,new SystemClock()).TickAsync(2,TimeSpan.FromMinutes(20),Token);
        Assert.Equal(ImageBuildState.Failed,(await store.BuildAsync(build!.Id,Token))!.State);Assert.Null(await service.SnapshotAsync(image.Id,build.Id,[],Token));
    }
    [Fact]
    public async Task Concurrency_limit_keeps_excess_builds_queued()
    {
        var (store,service)=Services();var image=await service.CreateAsync(Request(),Token);
        for(var i=0;i<3;i++)await service.QueueAsync(image.Id,1,Token);
        var runtime=new ObservationRuntime(new(ImageBuildState.Building));
        await new ImageBuildCoordinator(store,runtime,new SystemClock()).TickAsync(2,TimeSpan.FromMinutes(20),Token);
        Assert.Equal(2,runtime.Calls);Assert.Single(await store.BuildsAsync(null,Token),x=>x.State==ImageBuildState.Queued);
    }
    [Fact]
    public async Task Expired_builds_time_out_without_launching_new_work()
    {
        var (store,service)=Services();var image=await service.CreateAsync(Request(),Token);var q=await service.QueueAsync(image.Id,1,Token);var build=(await store.BuildAsync(q!.Id,Token))!;
        await store.UpdateBuildAsync(build with {StartedAt=DateTimeOffset.UtcNow.AddHours(-1),Revision=2},1,Token);
        var runtime=new ObservationRuntime(new(ImageBuildState.Ready,Reference:Digest));
        await new ImageBuildCoordinator(store,runtime,new SystemClock()).TickAsync(2,TimeSpan.FromMinutes(20),Token);
        Assert.Equal(0,runtime.Calls);Assert.Equal(ImageBuildState.TimedOut,(await store.BuildAsync(q.Id,Token))!.State);
    }
    [Theory]
    [InlineData("../outside")][InlineData("/etc/passwd")][InlineData("folder/../outside")][InlineData("folder\\outside")]
    public void Context_paths_cannot_escape_repository(string path)
        =>Assert.Throws<ArgumentException>(()=>ImageService.Validate(Request() with {Source=new("", "https://github.com/org/repo",ContextPath:path)}));
    [Theory]
    [InlineData("https://token@github.com/org/repo")][InlineData("http://github.com/org/repo")][InlineData("https://github.com/org/repo?token=secret")]
    public void Source_urls_cannot_embed_credentials(string url)
        =>Assert.Throws<ArgumentException>(()=>ImageService.Validate(Request() with {Source=new("",url)}));
    [Fact]
    public async Task Save_resolves_exact_build_and_ignores_forged_client_digest()
    {
        var (store,service)=Services();var image=await service.CreateAsync(Request(),Token);var q=await service.QueueAsync(image.Id,1,Token);var b=(await store.BuildAsync(q!.Id,Token))!;
        await store.UpdateBuildAsync(b with{State=ImageBuildState.Ready,Reference=Digest,Revision=2},1,Token);
        var document=Document() with { Steps=[new("plan","builtins.plan",ImageSelection:new("managed",image.Id,b.Id),ImageSnapshot:new(image.Id,b.Id,1,"Forged","invalid"))] };
        var resolved=await ImageDefinitions.ResolveAsync(document,service,new(["pull"]),Token);
        Assert.True(resolved.Validation.IsValid);Assert.Equal(Digest,resolved.Document.Steps[0].ImageSnapshot!.Reference);Assert.Equal("Tools",resolved.Document.Steps[0].ImageSnapshot!.Name);
        Assert.False(ImageDefinitions.ValidateRuntime(document).IsValid);
    }
    [Theory]
    [InlineData("inherit")][InlineData("platform")][InlineData("managed")]
    public void Image_override_preserves_other_environment_settings_and_identity(string mode)
    {
        var config=new EnvironmentConfiguration {Runtime=new(60),Image=new("registry.example/inherited:tag","Always",["inherited"]),Tools=[new("tool","echo ready")],McpServers=[new("mcp","stdio","echo")]};
        var environment=new EnvironmentSnapshot("env",2,"Environment","",config);
        var selection=new ImageSelection(mode,mode=="managed"?"image":null,mode=="managed"?"build":null);
        var snapshot=mode=="managed"?new PreparedImageSnapshot("image","build",1,"Prepared",Digest,PullSecretNames:["pull"]):null;
        var step=new WorkflowDefinitionStep("plan","builtins.plan",EnvironmentId:"env",EnvironmentSnapshot:environment,ImageSelection:selection,ImageSnapshot:snapshot);
        var document=Document() with {Steps=[step]};var effective=ImageDefinitions.EffectiveEnvironment(document,step)!;
        Assert.Equal(config.Runtime,effective.Configuration.Runtime);Assert.Equal(config.Tools,effective.Configuration.Tools);Assert.Equal(config.McpServers,effective.Configuration.McpServers);Assert.Equal("env",effective.Id);
        Assert.Equal("registry.example/inherited:tag",environment.Configuration.Image!.Reference);
        if(mode=="platform")Assert.Null(effective.Configuration.Image);
        else Assert.Equal(mode=="managed"?Digest:config.Image.Reference,effective.Configuration.Image!.Reference);
    }
    [Fact]
    public void Jobs_keep_source_credentials_and_agent_resources_out_of_builder()
    {
        var options=new ManagedImageOptions {Enabled=true,Registry="registry.example",WorkerBaseImage="worker@sha256:"+new string('a',64),PullSecretNames=["pull"]};
        var build=new ImageBuild {ImageId="image",ImageName="Tool",SourceRevision=1,SourceJson=System.Text.Json.JsonSerializer.Serialize(new ImageSource("","https://github.com/org/repo"),ImageJson.Options),CommitSha=new string('a',40)};
        var job=KubernetesImageBuildRuntime.CreateJob(build,options,"builds",false);var pod=job.Spec.Template.Spec;
        Assert.False(pod.AutomountServiceAccountToken);Assert.All(pod.Volumes,v=>Assert.Null(v.HostPath));Assert.DoesNotContain(pod.Volumes,v=>v.PersistentVolumeClaim is not null);
        Assert.Contains(pod.InitContainers[0].Env,x=>x.Name=="SOURCE_TOKEN");Assert.DoesNotContain(pod.Containers[0].Env,x=>x.Name=="SOURCE_TOKEN");Assert.DoesNotContain(pod.Containers[0].VolumeMounts,x=>x.Name=="input");Assert.NotEqual(true,pod.Containers[0].SecurityContext.Privileged);
        Assert.Equal(0,job.Spec.BackoffLimit);Assert.Equal(1200,job.Spec.ActiveDeadlineSeconds);
    }
    [Fact]
    public async Task Registry_tokens_enforce_read_only_pull_and_build_repository_scope()
    {
        using var rsa=RSA.Create(2048);var request=new CertificateRequest("CN=Formicae test signing",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
        var root=Path.Combine(Path.GetTempPath(),"formicae-token-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            var certPath=Path.Combine(root,"cert.pem");var keyPath=Path.Combine(root,"key.pem");await File.WriteAllTextAsync(certPath,cert.ExportCertificatePem());await File.WriteAllTextAsync(keyPath,rsa.ExportPkcs8PrivateKeyPem());
            var tokens=new RegistryTokens(Options.Create(new ManagedImageOptions {Enabled=true,PullPassword=new string('p',32),SigningCertificatePath=certPath,SigningKeyPath=keyPath}));
            static string Basic(string user,string password)=>"Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes(user+":"+password));
            var pull=await tokens.AuthorizeAsync(Basic("agent",new string('p',32)),"formicae-registry",["repository:formicae/local/image:pull,push"]);
            Assert.NotNull(pull);var claims=new JsonWebToken(pull);Assert.True(claims.TryGetPayloadValue<System.Text.Json.JsonElement>("access",out var access));Assert.Equal("pull",access[0].GetProperty("actions")[0].GetString());Assert.Single(access[0].GetProperty("actions").EnumerateArray());
            var password=tokens.BuildPassword("formicae/local/image");Assert.NotNull(await tokens.AuthorizeAsync(Basic("build",password),"formicae-registry",["repository:formicae/local/image:push"]));Assert.Null(await tokens.AuthorizeAsync(Basic("build",password),"formicae-registry",["repository:formicae/local/other:push"]));
            Assert.Null(await tokens.AuthorizeAsync(Basic("agent","wrong"),"formicae-registry",["repository:formicae/local/image:pull"]));
        }finally{Directory.Delete(root,true);}
    }
    private static WorkflowDefinitionDocument Document()=>new(DefaultWorkflowDefinitions.V1Alpha3Schema,"plan",[new("plan","builtins.plan")]);
    private sealed class ObservationRuntime(ImageBuildObservation result):IImageBuildRuntime
    { public int Calls;public Task<ImageBuildObservation> ReconcileAsync(ImageBuild b,CancellationToken t){Calls++;return Task.FromResult(result);}public Task CleanupAsync(ImageBuild b,CancellationToken t)=>Task.CompletedTask; }
    private sealed class RacingRuntime(Func<Task<ImageBuildResponse?>> cancel):IImageBuildRuntime
    {public bool Cleaned;public async Task<ImageBuildObservation> ReconcileAsync(ImageBuild b,CancellationToken t){await cancel();return new(ImageBuildState.Ready,Reference:Digest);}public Task CleanupAsync(ImageBuild b,CancellationToken t){Cleaned=true;return Task.CompletedTask;} }
}
