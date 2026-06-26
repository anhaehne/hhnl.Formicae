namespace hhnl.Formicae.Api;

public sealed record GitHubUserRepositoryResponse(
    string Owner,
    string Name,
    string RepositoryUrl,
    string DefaultBranch,
    bool Private);
