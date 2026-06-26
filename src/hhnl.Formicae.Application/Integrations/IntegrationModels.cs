namespace hhnl.Formicae.Application.Integrations;

public enum DevOpsProviderType
{
    GitHub = 0
}

public enum IntegrationCapability
{
    WorkItems = 0,
    SourceControl = 1,
    Webhooks = 2,
    IdentityProvider = 3
}

public sealed class DevOpsIntegration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DevOpsProviderType ProviderType { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string GitHubAppClientId { get; set; } = string.Empty;
    public string? GitHubAppClientSecretReference { get; set; }
    public string? GitHubOAuthAccessToken { get; set; }
    public string WebhookSecret { get; set; } = string.Empty;
    public string WebhookUrl { get; set; } = string.Empty;
    public bool IdentityProviderEnabled { get; set; }
    public bool RequiresRestart { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ConnectedRepository> Repositories { get; set; } = [];
}

public sealed class ConnectedRepository
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DevOpsIntegrationId { get; set; }
    public DevOpsIntegration? DevOpsIntegration { get; set; }
    public string Owner { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string RepositoryUrl { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";
    public long? InstallationId { get; set; }
    public string? InstallationAccount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
