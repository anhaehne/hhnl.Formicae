using hhnl.Formicae.Application.Images;
using hhnl.Formicae.Infrastructure.Images;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Api;

public static class ImageEndpoints
{
    public static void MapImageEndpoints(this WebApplication app)
    {
        var group=app.MapGroup("/api/images");
        group.MapGet("",async(ImageService service,CancellationToken t)=>Results.Ok(await service.ListAsync(t))).RequireAuthorization(ManagementAuthorization.WorkflowView);
        group.MapGet("/configuration",(IOptions<ManagedImageOptions> o,IConfiguration config)=>Results.Ok(new { enabled=o.Value.Enabled||config.GetValue("UseFakeAdapters",true), developmentAdapter=config.GetValue("UseFakeAdapters",true),workerBaseImage=o.Value.WorkerBaseImage,pullSecretNames=o.Value.PullSecretNames })).RequireAuthorization(ManagementAuthorization.WorkflowView);
        group.MapGet("/{id}",async(string id,ImageService service,CancellationToken t)=>await service.GetAsync(id,t) is {} image?Results.Ok(image):Results.NotFound()).RequireAuthorization(ManagementAuthorization.WorkflowView);
        group.MapGet("/{id}/revisions",async(string id,ImageService service,CancellationToken t)=>Results.Ok(await service.RevisionsAsync(id,t))).RequireAuthorization(ManagementAuthorization.WorkflowView);
        group.MapPost("",async(ImageRequest r,ImageService service,CancellationToken t)=>{
            try {var image=await service.CreateAsync(r,t);return Results.Created("/api/images/"+image.Id,image);}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}
        }).RequireAuthorization(ManagementAuthorization.ManagementAdmin);
        group.MapPut("/{id}",async(string id,ImageRequest r,ImageService service,CancellationToken t)=>{
            try{return await service.UpdateAsync(id,r,t) is {} image?Results.Ok(image):Results.NotFound();}catch(ImageConflictException e){return Results.Conflict(new{error=e.Message});}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}
        }).RequireAuthorization(ManagementAuthorization.ManagementAdmin);
        group.MapDelete("/{id}",async(string id,int expectedRevision,ImageService service,CancellationToken t)=>{
            try{return await service.ArchiveAsync(id,expectedRevision,t)?Results.NoContent():Results.NotFound();}catch(ImageConflictException e){return Results.Conflict(new{error=e.Message});}
        }).RequireAuthorization(ManagementAuthorization.ManagementAdmin);
        group.MapGet("/{id}/builds",async(string id,ImageService service,CancellationToken t)=>Results.Ok(await service.BuildsAsync(id,t))).RequireAuthorization(ManagementAuthorization.WorkflowView);
        group.MapGet("/{id}/builds/{buildId}",async(string id,string buildId,ImageService service,CancellationToken t)=>await service.GetBuildAsync(id,buildId,t) is {} b?Results.Ok(b):Results.NotFound()).RequireAuthorization(ManagementAuthorization.WorkflowView);
        group.MapPost("/{id}/builds",async(string id,QueueImageBuildRequest r,ImageService service,IOptions<ManagedImageOptions> options,IConfiguration configuration,CancellationToken t)=>{
            if(!configuration.GetValue("UseFakeAdapters",true)&&!options.Value.Enabled)return Results.Conflict(new{error="Managed image builds are disabled. Ask an operator to configure the registry and build namespace."});
            try{return await service.QueueAsync(id,r.ExpectedRevision,t) is {} b?Results.Accepted($"/api/images/{id}/builds/{b.Id}",b):Results.NotFound();}catch(ImageConflictException e){return Results.Conflict(new{error=e.Message});}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}catch(InvalidOperationException){return Results.BadRequest(new{error="Repository ref could not be resolved. Check the connected repository and branch."});}
        }).RequireAuthorization(ManagementAuthorization.ManagementAdmin);
        group.MapPost("/{id}/builds/{buildId}/cancel",async(string id,string buildId,ImageService service,CancellationToken t)=>{
            try{return await service.CancelAsync(id,buildId,t) is {} b?Results.Ok(b):Results.NotFound();}catch(ImageConflictException e){return Results.Conflict(new{error=e.Message});}
        }).RequireAuthorization(ManagementAuthorization.ManagementAdmin);
        app.MapGet("/api/registry/token",async(HttpContext context,RegistryTokens tokens)=>{
            var query=context.Request.Query;
            var jwt=await tokens.AuthorizeAsync(context.Request.Headers.Authorization.ToString(),query["service"].ToString(),query["scope"].Select(x=>x!).ToArray());
            if(jwt is null){context.Response.Headers.WWWAuthenticate="Basic realm=\"Formicae registry\"";return Results.Unauthorized();}
            return Results.Ok(new{token=jwt,expires_in=300,issued_at=DateTimeOffset.UtcNow});
        }).AllowAnonymous();
    }
    public sealed record QueueImageBuildRequest(int ExpectedRevision);
}
