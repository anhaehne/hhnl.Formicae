using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowExtensionValidationTests
{
    private static EnvironmentSnapshot Profile(EnvironmentConfiguration? configuration = null) => new("profile", 3, "Extended", "", configuration ?? new()
    {
        Image = new("registry.test/worker:1", PullSecretNames: ["registry-key"]),
        Tools = [new("curl", "echo install")], McpServers = [new("docs", Command: "npx", Arguments: ["docs-server"], EnvironmentVariables: new Dictionary<string,string> { ["TOKEN"] = "DOCS_TOKEN" })]
    });
    private static WorkflowDefinitionStep Step(string uses = "builtins.implement") => new("task", uses, SecretReferences: [new("DOCS_TOKEN", "docs-secret", "token")]);

    [Fact]
    public void Typed_environment_roundtrips_with_all_extensions_and_runtime_snapshot_is_pinned()
    {
        var profile = Profile();
        Assert.True(EnvironmentDefinitions.ValidateConfiguration(profile.Configuration).IsValid);
        var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var restored = JsonSerializer.Deserialize<EnvironmentSnapshot>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(profile.Configuration.Image!.Reference, restored!.Configuration.Image!.Reference);
        Assert.Equal(profile.Configuration.Image.PullSecretNames, restored.Configuration.Image.PullSecretNames);
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "task", [Step() with { EnvironmentSnapshot = profile }], DefaultEnvironmentId: "profile", DefaultEnvironmentSnapshot: profile);
        Assert.True(EnvironmentDefinitions.ValidateRuntime(document).IsValid);
        Assert.Same(profile, EnvironmentDefinitions.ResolveForTask(document, document.Steps[0]));
    }

    [Fact]
    public async Task Environment_api_roundtrips_extensions_and_rejects_raw_credential_fields()
    {
        await using var factory = new ManagementAuthApiTests.FormicaeApiFactory(true);
        var client = factory.CreateAuthenticatedClient((await factory.CreateAdminAsync("extension-admin")).Id);
        var response = await client.PostAsJsonAsync("/api/environments", new CreateEnvironmentRequest("Extended", Configuration: Profile().Configuration));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<EnvironmentResponse>())!;
        Assert.Equal("registry.test/worker:1", saved.Configuration.Image!.Reference);
        Assert.Equal("echo install", Assert.Single(saved.Configuration.Tools).Script);
        Assert.Equal("DOCS_TOKEN", Assert.Single(saved.Configuration.McpServers).EnvironmentVariables!["TOKEN"]);
        var raw = "{\"name\":\"Credentials\",\"configuration\":{\"mcpServers\":[{\"name\":\"docs\",\"command\":\"docs\",\"secretValue\":\"hidden\"}]}}";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/environments", new StringContent(raw, Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Theory]
    [InlineData("image-policy")]
    [InlineData("image-double-colon")]
    [InlineData("image-empty-segment")]
    [InlineData("image-empty-tag")]
    [InlineData("image-uppercase-repository")]
    [InlineData("image-long-tag")]
    [InlineData("image-short-digest")]
    [InlineData("image-pull-secret-long-label")]
    [InlineData("image-pull-secret-bad-label")]
    [InlineData("image-credentials")]
    [InlineData("null-tool")]
    [InlineData("tool-duplicate")]
    [InlineData("tool-timeout")]
    [InlineData("mcp-url-credentials")]
    [InlineData("mcp-url-query")]
    [InlineData("mcp-duplicate")]
    [InlineData("mcp-command")]
    [InlineData("mcp-reserved-alias")]
    [InlineData("mcp-authorization-conflict")]
    [InlineData("mcp-http-environment")]
    public void Invalid_extension_configuration_is_rejected(string scenario)
    {
        var config = scenario switch
        {
            "image-double-colon" => new() { Image = new("ubuntu::tag") },
            "image-empty-segment" => new() { Image = new("foo//bar") },
            "image-empty-tag" => new() { Image = new("ubuntu:") },
            "image-uppercase-repository" => new() { Image = new("registry.test/Uppercase:tag") },
            "image-long-tag" => new() { Image = new("ubuntu:" + new string('a', 129)) },
            "image-short-digest" => new() { Image = new("ubuntu@sha256:" + new string('a', 63)) },
            "image-pull-secret-long-label" => new() { Image = new("ubuntu:tag", PullSecretNames: [new string('a', 64)]) },
            "image-pull-secret-bad-label" => new() { Image = new("ubuntu:tag", PullSecretNames: ["valid.-invalid"]) },
            "image-policy" => new EnvironmentConfiguration { Image = new("worker:1", "sometimes") },
            "image-credentials" => new() { Image = new("https://user:pass@registry/worker:1") },
            "null-tool" => new() { Tools = [null!] },
            "tool-duplicate" => new() { Tools = [new("curl", "echo a"), new("curl", "echo b")] },
            "tool-timeout" => new() { Tools = [new("curl", "echo a", TimeoutSeconds: 3601)] },
            "mcp-url-credentials" => new() { McpServers = [new("docs", "http", Url: "https://user:pass@example.test/mcp")] },
            "mcp-url-query" => new() { McpServers = [new("docs", "http", Url: "https://example.test/mcp?token=hidden")] },
            "mcp-duplicate" => new() { McpServers = [new("docs", Command: "docs"), new("docs", Command: "docs")] },
            "mcp-command" => new() { McpServers = [new("docs")] },
            "mcp-http-environment" => new() { McpServers = [new("docs", "http", Url: "https://example.test/mcp", EnvironmentVariables: new Dictionary<string,string> { ["TOKEN"] = "STEP_TOKEN" })] },
            "mcp-authorization-conflict" => new() { McpServers = [new("docs", "http", Url: "https://example.test/mcp", BearerTokenEnvironmentVariable: "TOKEN", HeaderEnvironmentVariables: new Dictionary<string,string> { ["Authorization"] = "TOKEN" })] },
            _ => new() { McpServers = [new("docs", Command: "docs", EnvironmentVariables: new Dictionary<string,string> { ["TOKEN"] = "OPENAI_API_KEY" })] }
        };
        Assert.False(EnvironmentDefinitions.ValidateConfiguration(config).IsValid);
    }

    [Theory]
    [InlineData("localhost:5000/team/image:Release_1")]
    [InlineData("registry.example.test:443/team/image__name:Tag")]
    [InlineData("[2001:db8::1]:5000/team/image:Tag")]
    [InlineData("digest")]
    public void Valid_registry_port_tag_and_sha256_digest_are_accepted(string reference)
    {
        if (reference == "digest") reference = "registry.test/team/worker:release@sha256:" + new string('a', 64);
        Assert.True(EnvironmentDefinitions.ValidateConfiguration(new() { Image = new(reference, PullSecretNames: ["registry.auth"]) }).IsValid);
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("HOME")]
    [InlineData("FORMICAE_TASK")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("LLM_API_KEY")]
    [InlineData("CODEX_HOME")]
    [InlineData("invalid-name")]
    [InlineData("BASH_ENV")]
    [InlineData("ENV")]
    [InlineData("GIT_CONFIG_COUNT")]
    public void Secret_aliases_cannot_override_worker_or_provider_configuration(string alias)
        => Assert.False(WorkflowExecutionExtensions.ValidateStep(Step() with { SecretReferences = [new(alias, "secret", "token")] }).IsValid);

    [Fact]
    public void Capabilities_default_inherit_profile_and_explicit_empty_disables_managed_resources()
    {
        Assert.Equal(new[] { "browser", "nested-containers", "tool:curl", "mcp:docs" }, WorkflowExecutionExtensions.ResolveCapabilities(Step(), Profile()));
        Assert.Equal(new[] { "tool:curl", "mcp:docs" }, WorkflowExecutionExtensions.ResolveCapabilities(Step("builtins.plan"), Profile()));
        Assert.Empty(WorkflowExecutionExtensions.ResolveCapabilities(Step() with { Capabilities = [] }, Profile()));
        Assert.Equal(new[] { "tool:curl" }, WorkflowExecutionExtensions.ResolveCapabilities(Step(WorkflowExecutionExtensions.ScriptUses), Profile()));
    }

    [Fact]
    public void Enabled_mcp_requires_each_selected_secret_alias_and_disabled_mcp_requires_none()
    {
        Assert.True(WorkflowExecutionExtensions.ValidateStep(Step(), Profile()).IsValid);
        Assert.False(WorkflowExecutionExtensions.ValidateStep(Step() with { SecretReferences = [] }, Profile()).IsValid);
        Assert.True(WorkflowExecutionExtensions.ValidateStep(Step() with { SecretReferences = [], Capabilities = [] }, Profile()).IsValid);
        Assert.False(WorkflowExecutionExtensions.ValidateStep(Step() with { Capabilities = ["tool:missing"] }, Profile()).IsValid);
        Assert.False(WorkflowExecutionExtensions.ValidateStep(Step() with { SecretReferences = [new("DOCS_TOKEN", "secret", "key"), new("docs_token", "secret", "other")] }, Profile()).IsValid);
    }

    [Fact]
    public void Builtin_default_and_conflicting_snapshots_cannot_smuggle_extensions()
    {
        var modified = EnvironmentService.DefaultSnapshot with { Configuration = new() { Image = new("worker:custom") } };
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "task", [Step()], DefaultEnvironmentSnapshot: modified);
        Assert.False(EnvironmentDefinitions.ValidateRuntime(document).IsValid);
        var original = Profile(); var altered = original with { Configuration = original.Configuration with { Tools = [new("curl", "echo altered")] } };
        document = document with { DefaultEnvironmentId = "profile", DefaultEnvironmentSnapshot = original, Steps = [Step() with { EnvironmentId = "profile", EnvironmentSnapshot = altered }] };
        Assert.Contains(EnvironmentDefinitions.ValidateRuntime(document).Errors, error => error.Code == "definition.environment.snapshot.conflict");
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("directory")]
    [InlineData("timeout")]
    [InlineData("empty")]
    [InlineData("model")]
    [InlineData("browser")]
    [InlineData("mcp")]
    public void Script_configuration_rejects_invalid_execution_settings(string scenario)
    {
        var step = new WorkflowDefinitionStep("script", WorkflowExecutionExtensions.ScriptUses, Script: new("echo hello"));
        step = scenario switch
        {
            "shell" => step with { Script = step.Script! with { Shell = "pwsh" } },
            "directory" => step with { Script = step.Script! with { WorkingDirectory = "/etc" } },
            "timeout" => step with { Script = step.Script! with { TimeoutSeconds = 0 } },
            "empty" => step with { Script = step.Script! with { Script = " " } },
            "model" => step with { Model = "model" },
            "browser" => step with { Capabilities = ["browser"] },
            _ => step with { Capabilities = ["mcp:docs"] }
        };
        Assert.False(new WorkflowDefinitionValidator().Validate(new(DefaultWorkflowDefinitions.V1Alpha3Schema, "script", [step])).IsValid);
    }
}
