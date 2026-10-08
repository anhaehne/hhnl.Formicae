using System.Net;
using System.Net.Http.Json;
using hhnl.Formicae.Application.Images;
using hhnl.Formicae.Api;

namespace hhnl.Formicae.Tests;

public sealed class ManagedImageApiTests
{
    [Fact]
    public async Task Admin_can_save_queue_cancel_and_archive_with_revision_conflicts()
    {
        await using var factory=new ManagementAuthApiTests.FormicaeApiFactory(true);
        var client=factory.CreateAuthenticatedClient((await factory.CreateAdminAsync("image-admin")).Id);
        var response=await client.PostAsJsonAsync("/api/images",new ImageRequest("Tools",null,new("FROM worker")));
        Assert.Equal(HttpStatusCode.Created,response.StatusCode);var image=(await response.Content.ReadFromJsonAsync<ImageResponse>())!;
        Assert.Equal(HttpStatusCode.Conflict,(await client.PutAsJsonAsync("/api/images/"+image.Id,new ImageRequest("Tools",null,new("FROM worker"),2))).StatusCode);
        response=await client.PostAsJsonAsync($"/api/images/{image.Id}/builds",new ImageEndpoints.QueueImageBuildRequest(1));
        Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);var build=(await response.Content.ReadFromJsonAsync<ImageBuildResponse>())!;
        Assert.NotNull(response.Headers.Location);Assert.Equal(image.Id,build.ImageId);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync($"/api/images/other/builds/{build.Id}")).StatusCode);
        var cancelled=await client.PostAsync($"/api/images/{image.Id}/builds/{build.Id}/cancel",null);Assert.Equal(HttpStatusCode.OK,cancelled.StatusCode);
        Assert.Equal("Cancelled",(await cancelled.Content.ReadFromJsonAsync<ImageBuildResponse>())!.State);
        Assert.Equal(HttpStatusCode.NoContent,(await client.DeleteAsync($"/api/images/{image.Id}?expectedRevision=1")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<ImageResponse[]>("/api/images"))!);
        Assert.Single((await client.GetFromJsonAsync<ImageBuildResponse[]>($"/api/images/{image.Id}/builds"))!);
    }
    [Fact]
    public async Task Viewer_can_inspect_but_cannot_build_cancel_edit_or_archive()
    {
        await using var factory=new ManagementAuthApiTests.FormicaeApiFactory(true);
        var client=factory.CreateAuthenticatedClient((await factory.CreateViewerAsync("image-viewer")).Id);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/images")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/images",new ImageRequest("Tools",null,new("FROM worker")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/images/id/builds",new ImageEndpoints.QueueImageBuildRequest(1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsync("/api/images/id/builds/build/cancel",null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.DeleteAsync("/api/images/id?expectedRevision=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/registry/token?service=formicae-registry&scope=repository:formicae/local/image:push")).StatusCode);
    }
}
