using System.Net;
using System.Net.Http.Json;
using System.Text;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Tests;

public sealed class EnvironmentApiTests
{
    [Fact]
    public async Task Administrator_manages_environment_catalog_with_conflict_feedback()
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(true);
        var client = factory.CreateAuthenticatedClient((await factory.CreateAdminAsync("environment-admin")).Id);
        var created = await client.PostAsJsonAsync("/api/environments", new CreateEnvironmentRequest("Short", Configuration: new() { Runtime = new(20) }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var first = (await created.Content.ReadFromJsonAsync<EnvironmentResponse>())!;
        Assert.EndsWith($"/api/environments/{first.Id}", created.Headers.Location!.ToString()); Assert.Equal(20, first.Configuration.Runtime!.TimeoutLimitSeconds);
        var updated = await client.PutAsJsonAsync($"/api/environments/{first.Id}", new UpdateEnvironmentRequest(1, "Longer", Configuration: new() { Runtime = new(30) }));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode); Assert.Equal(2, (await updated.Content.ReadFromJsonAsync<EnvironmentResponse>())!.Revision);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/environments/{first.Id}", new UpdateEnvironmentRequest(1, "stale"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/environments/{first.Id}?expectedRevision=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/environments/{first.Id}?expectedRevision=2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/environments/{first.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync("/api/environments/default?expectedRevision=1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/environments/default", new UpdateEnvironmentRequest(1, "changed"))).StatusCode);
    }

    [Theory]
    [InlineData("{\"runtime\":{\"timeoutLimitSeconds\":\"10\"}}")]
    [InlineData("{\"runtime\":{\"timeoutLimitSeconds\":1.5}}")]
    [InlineData("{\"runtime\":{\"privileged\":true}}")]
    [InlineData("{\"unexpected\":true}")]
    [InlineData("{\"tools\":null}")]
    [InlineData("{\"image\":\"worker:latest\"}")]
    public async Task Malformed_or_unsupported_configuration_is_rejected_at_api_boundary(string configuration)
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(true);
        var client = factory.CreateAuthenticatedClient((await factory.CreateAdminAsync("environment-invalid")).Id);
        var response = await client.PostAsync("/api/environments", new StringContent("{\"name\":\"Invalid\",\"configuration\":" + configuration + "}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(Assert.Single((await client.GetFromJsonAsync<EnvironmentResponse[]>("/api/environments"))!).BuiltIn);
    }

    [Fact]
    public async Task Viewer_inspects_default_but_cannot_mutate_catalog()
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(true);
        var client = factory.CreateAuthenticatedClient((await factory.CreateViewerAsync("environment-viewer")).Id);
        Assert.True(Assert.Single((await client.GetFromJsonAsync<EnvironmentResponse[]>("/api/environments"))!).BuiltIn);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/environments/default")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/environments", new CreateEnvironmentRequest("new"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/environments/default", new UpdateEnvironmentRequest(1, "changed"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/environments/default?expectedRevision=1")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Catalog_requires_workflow_view_permission(bool authenticated)
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(true);
        var client = authenticated ? factory.CreateAuthenticatedClient((await factory.CreateUserAsync("environment-no-role")).Id) : factory.CreateClient();
        Assert.Equal(authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, (await client.GetAsync("/api/environments")).StatusCode);
    }
}
