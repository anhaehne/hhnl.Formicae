namespace hhnl.Formicae.Application.Workflows;

public sealed class OpenHandsOptions
{
    public string AuthMethod { get; set; } = OpenHandsAuthMethods.ApiKey;
    public string? Provider { get; set; }
    public string? DefaultModel { get; set; }
    public string? EndpointUrl { get; set; }
    public string LlmApiKeySecretName { get; set; } = "openhands-llm-api-key";
    public string Shell { get; set; } = "/bin/sh";
    public string BootstrapCommand { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string CodexSubscriptionImage { get; set; } = string.Empty;
    public string CodexSubscriptionBootstrapCommand { get; set; } = string.Empty;
    public string CodexSubscriptionCommand { get; set; } = string.Empty;
}

public static class OpenHandsAuthMethods
{
    public const string ApiKey = "ApiKey";
    public const string CodexSubscription = "CodexSubscription";
}
