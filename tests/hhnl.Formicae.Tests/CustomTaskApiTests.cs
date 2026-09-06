using System.Net;
using System.Net.Http.Json;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Tests;

public sealed class CustomTaskApiTests
{
    [Fact]
    public async Task Administrator_manages_catalog_with_validation_and_conflict_feedback()
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(true);
        var user = await factory.CreateAdminAsync("task-admin"); var client = factory.CreateAuthenticatedClient(user.Id);
        var created = await client.PostAsJsonAsync("/api/custom-tasks", new CreateCustomTaskRequest("Summarize", "Summarize {{input.text}}", Inputs: [new("text", "string", true)]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var first = (await created.Content.ReadFromJsonAsync<CustomTaskResponse>())!;
        Assert.EndsWith($"/api/custom-tasks/{first.Id}", created.Headers.Location!.ToString());
        Assert.Equal("agent", first.Runner.Kind); Assert.True(Assert.Single(first.Inputs).Required);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/custom-tasks", new CreateCustomTaskRequest("Bad", "{{unknown}}"))).StatusCode);
        var updated = await client.PutAsJsonAsync($"/api/custom-tasks/{first.Id}", new UpdateCustomTaskRequest(1, "New", "Prompt"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode); Assert.Equal(2, (await updated.Content.ReadFromJsonAsync<CustomTaskResponse>())!.Revision);
        var stale = await client.PutAsJsonAsync($"/api/custom-tasks/{first.Id}", new UpdateCustomTaskRequest(1, "Stale", "Prompt"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); Assert.Contains("error", await stale.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/custom-tasks/{first.Id}?expectedRevision=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/custom-tasks/{first.Id}?expectedRevision=2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/custom-tasks/{first.Id}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<CustomTaskResponse[]>("/api/custom-tasks"))!);
    }

    [Fact]
    public async Task Viewer_inspects_catalog_but_cannot_mutate_it()
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(true);
        var admin = factory.CreateAuthenticatedClient((await factory.CreateAdminAsync("task-owner")).Id);
        var created = await admin.PostAsJsonAsync("/api/custom-tasks", new CreateCustomTaskRequest("Review", "Review"));
        var task = (await created.Content.ReadFromJsonAsync<CustomTaskResponse>())!;
        var client = factory.CreateAuthenticatedClient((await factory.CreateViewerAsync("task-viewer")).Id);
        Assert.Single((await client.GetFromJsonAsync<CustomTaskResponse[]>("/api/custom-tasks"))!);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/custom-tasks/{task.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/custom-tasks", new CreateCustomTaskRequest("new", "prompt"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/custom-tasks/{task.Id}", new UpdateCustomTaskRequest(1, "changed", "prompt"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/custom-tasks/{task.Id}?expectedRevision=1")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Catalog_requires_workflow_view_permission(bool authenticated)
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(true);
        var client = authenticated ? factory.CreateAuthenticatedClient((await factory.CreateUserAsync("task-no-role")).Id) : factory.CreateClient();
        Assert.Equal(authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, (await client.GetAsync("/api/custom-tasks")).StatusCode);
    }
}
