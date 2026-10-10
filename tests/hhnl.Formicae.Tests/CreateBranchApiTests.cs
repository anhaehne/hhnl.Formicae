using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using hhnl.Formicae.Application.Integrations;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace hhnl.Formicae.Tests;

public sealed class CreateBranchApiTests
{
    [Theory]
    [InlineData("success", HttpStatusCode.OK)]
    [InlineData("failure", HttpStatusCode.BadGateway)]
    [InlineData("wrong-provider", HttpStatusCode.BadRequest)]
    [InlineData("wrong-repository", HttpStatusCode.NotFound)]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    public async Task Branch_discovery_is_repository_scoped_and_authorized(string scenario, HttpStatusCode expected)
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(true);
        var platform = DispatchProxy.Create<IDevOpsPlatform, Branches>();
        ((Branches)(object)platform).Fail = scenario == "failure";
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IDevOpsPlatformFactory>(new Factory(platform))));
        var user = scenario == "viewer" ? await factory.CreateViewerAsync("branches-viewer") : await factory.CreateAdminAsync("branches-admin");
        using var scope = host.Services.CreateScope();
        var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Test-UserId", user.Id);
        var store = scope.ServiceProvider.GetRequiredService<IDevOpsIntegrationStore>();
        var integration = await store.CreateAsync(new() { ProviderType = scenario == "wrong-provider" ? DevOpsProviderType.Gitea : DevOpsProviderType.GitHub, DisplayName = "Branches" }, default);
        var repository = await store.AddRepositoryAsync(new() { DevOpsIntegrationId = integration.Id, Owner = "acme", Name = "repo", RepositoryUrl = "https://github.com/acme/repo", DefaultBranch = "main" }, default);
        var id = scenario == "wrong-repository" ? Guid.NewGuid() : repository.Id;
        var response = await client.GetAsync($"/api/integrations/{integration.Id}/repositories/{id}/branches");
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK) Assert.Equal(new[] { "main", "release/stable" }, await response.Content.ReadFromJsonAsync<string[]>());
        if (scenario == "failure") Assert.DoesNotContain("private provider details", await response.Content.ReadAsStringAsync());
    }
    public class Branches : DispatchProxy
    {
        public bool Fail;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal(nameof(IDevOpsPlatform.ListBranchesAsync), method!.Name);
            Assert.Equal("https://github.com/acme/repo", ((DevOpsRepositoryReference)args![0]!).RepositoryUrl);
            return Fail ? Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("private provider details"))
                : Task.FromResult<IReadOnlyList<string>>(["main", "release/stable"]);
        }
    }
    private sealed class Factory(IDevOpsPlatform platform) : IDevOpsPlatformFactory
    {
        public Task<DevOpsPlatformContext> CreateForRepositoryAsync(string url, CancellationToken token)
            => Task.FromResult(new DevOpsPlatformContext(new(), new(), DevOpsReferenceParser.ParseRepositoryUrl(DevOpsProviderType.GitHub, url), platform));
    }
}
