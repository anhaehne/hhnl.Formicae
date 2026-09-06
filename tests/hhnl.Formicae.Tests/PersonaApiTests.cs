using System.Net;
using System.Net.Http.Json;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Tests;

public sealed class PersonaApiTests
{
    [Fact]
    public async Task Null_workflow_step_returns_validation_feedback_and_bad_request_instead_of_server_error()
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(true);
        var user = await factory.CreateAdminAsync("persona-malformed");
        var client = factory.CreateAuthenticatedClient(user.Id);
        var document = WorkflowDefinitionJson.Deserialize("""{"schema":"formicae.workflow/v1alpha3","startStepId":"plan","steps":[null]}""")!;
        var validationResponse = await client.PostAsJsonAsync("/api/workflow-definitions/validate", document);
        Assert.Equal(HttpStatusCode.OK, validationResponse.StatusCode);
        var validation = (await validationResponse.Content.ReadFromJsonAsync<WorkflowDefinitionValidationResult>())!;
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Code == "definition.step.required");
        var created = await client.PostAsJsonAsync("/api/workflow-definitions", new CreateWorkflowDefinitionRequest("Malformed workflow"));
        var definition = (await created.Content.ReadFromJsonAsync<WorkflowDefinitionResponse>())!;
        var saved = await client.PostAsJsonAsync($"/api/workflow-definitions/{definition.Id}/versions", new CreateWorkflowDefinitionVersionRequest(null, true, false, document));
        Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
        Assert.Contains("definition.step.required", await saved.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Administrator_creates_updates_and_deletes_with_conflict_responses()
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(true);
        var user = await factory.CreateAdminAsync("persona-admin"); var client = factory.CreateAuthenticatedClient(user.Id);
        var created = await client.PostAsJsonAsync("/api/personas", new CreatePersonaRequest("Tester", "Test changes"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var first = (await created.Content.ReadFromJsonAsync<PersonaResponse>())!;
        var updated = await client.PutAsJsonAsync($"/api/personas/{first.Id}", new UpdatePersonaRequest(1, "Tester 2"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(2, (await updated.Content.ReadFromJsonAsync<PersonaResponse>())!.Revision);
        var stale = await client.PutAsJsonAsync($"/api/personas/{first.Id}", new UpdatePersonaRequest(1, "stale"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); Assert.Contains("error", await stale.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/personas/{first.Id}?expectedRevision=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/personas/{first.Id}?expectedRevision=2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/personas/{first.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync("/api/personas/default?expectedRevision=1")).StatusCode);
    }
    [Fact]
    public async Task Viewer_can_inspect_but_cannot_mutate_personas()
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(true);
        var viewer = await factory.CreateViewerAsync("persona-viewer"); var client = factory.CreateAuthenticatedClient(viewer.Id);
        Assert.True(Assert.Single((await client.GetFromJsonAsync<PersonaResponse[]>("/api/personas"))!).BuiltIn);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/personas/default")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/personas", new CreatePersonaRequest("new"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/personas/default", new UpdatePersonaRequest(1, "changed"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/personas/default?expectedRevision=1")).StatusCode);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Catalog_requires_workflow_view_permission(bool authenticated)
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(true);
        var client = authenticated ? factory.CreateAuthenticatedClient((await factory.CreateUserAsync("persona-no-role")).Id) : factory.CreateClient();
        Assert.Equal(authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, (await client.GetAsync("/api/personas")).StatusCode);
    }
}
