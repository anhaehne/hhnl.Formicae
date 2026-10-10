using System.Net;
using System.Net.Http.Json;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowConcurrencyApiTests
{
    [Fact]
    public async Task Limits_default_to_unlimited_and_can_be_saved_and_cleared()
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(false);
        var client = factory.CreateClient();
        Assert.Null((await client.GetFromJsonAsync<WorkflowConcurrencySettings>("/api/workflow-concurrency"))!.MaxActiveInstances);
        var definition = (await (await client.PostAsJsonAsync("/api/workflow-definitions", new { name = "Limited" })).Content.ReadFromJsonAsync<WorkflowDefinitionResponse>())!;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/workflow-concurrency", new WorkflowConcurrencyLimit(3))).StatusCode);
        Assert.Equal(3, (await client.GetFromJsonAsync<WorkflowConcurrencySettings>("/api/workflow-concurrency"))!.MaxActiveInstances);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/workflow-definitions/{definition.Id}/concurrency", new WorkflowConcurrencyLimit(2))).StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<WorkflowDefinitionResponse>($"/api/workflow-definitions/{definition.Id}"))!.MaxActiveInstances);
        await client.PutAsJsonAsync("/api/workflow-concurrency", new WorkflowConcurrencyLimit(null));
        await client.PutAsJsonAsync($"/api/workflow-definitions/{definition.Id}/concurrency", new WorkflowConcurrencyLimit(null));
        Assert.Null((await client.GetFromJsonAsync<WorkflowConcurrencySettings>("/api/workflow-concurrency"))!.MaxActiveInstances);
        Assert.Null((await client.GetFromJsonAsync<WorkflowDefinitionResponse>($"/api/workflow-definitions/{definition.Id}"))!.MaxActiveInstances);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Rejects_nonpositive_limits(int limit)
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(false);
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/workflow-concurrency", new WorkflowConcurrencyLimit(limit))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/workflow-definitions/{Guid.NewGuid()}/concurrency", new WorkflowConcurrencyLimit(limit))).StatusCode);
    }

    [Fact]
    public async Task Limits_require_authenticated_access()
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(true);
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/workflow-concurrency")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/api/workflow-concurrency", new WorkflowConcurrencyLimit(1))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync($"/api/workflow-definitions/{Guid.NewGuid()}/concurrency", new WorkflowConcurrencyLimit(1))).StatusCode);
    }
}
