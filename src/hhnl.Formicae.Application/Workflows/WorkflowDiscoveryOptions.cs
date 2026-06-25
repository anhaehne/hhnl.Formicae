namespace hhnl.Formicae.Application.Workflows;

public sealed class WorkflowDiscoveryOptions
{
    public bool Enabled { get; set; }
    public string RepositoryUrl { get; set; } = string.Empty;
    public string BaseBranch { get; set; } = "main";
    public string? Model { get; set; }
}
