namespace hhnl.Formicae.Application.Integrations;

public sealed record DevOpsRepositoryReference(
    DevOpsProviderType ProviderType,
    string Owner,
    string Name,
    string RepositoryUrl,
    Uri ServerUrl);

public sealed record DevOpsIssueReference(
    DevOpsProviderType ProviderType,
    string Owner,
    string Repository,
    int Number,
    string IssueUrl,
    string RepositoryUrl,
    Uri ServerUrl);

public sealed record DevOpsPullRequestReference(
    DevOpsProviderType ProviderType,
    string Owner,
    string Repository,
    int Number,
    string PullRequestUrl,
    string RepositoryUrl,
    Uri ServerUrl);

public static class DevOpsReferenceParser
{
    private static readonly Uri GitHubServerUrl = new("https://github.com");

    public static DevOpsRepositoryReference ParseRepositoryUrl(
        DevOpsProviderType providerType,
        string repositoryUrl,
        string? serverUrl = null)
    {
        var server = GetServerUrl(providerType, serverUrl);
        var uri = ParseAbsoluteUri(repositoryUrl, nameof(repositoryUrl));
        EnsureServer(providerType, uri, server, repositoryUrl);

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new ArgumentException($"Expected a {providerType} repository URL like {BuildRepositoryExample(providerType, server)}.", nameof(repositoryUrl));
        }

        var name = NormalizeRepositoryName(parts[1]);
        if (string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException($"Expected a {providerType} repository URL like {BuildRepositoryExample(providerType, server)}.", nameof(repositoryUrl));
        }

        return new DevOpsRepositoryReference(providerType, parts[0], name, BuildRepositoryUrl(server, parts[0], name), server);
    }

    public static DevOpsIssueReference ParseIssueUrl(
        DevOpsProviderType providerType,
        string issueUrl,
        string? serverUrl = null)
    {
        var server = GetServerUrl(providerType, serverUrl);
        var uri = ParseAbsoluteUri(issueUrl, nameof(issueUrl));
        EnsureServer(providerType, uri, server, issueUrl);

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4 || !parts[2].Equals("issues", StringComparison.OrdinalIgnoreCase) || !int.TryParse(parts[3], out var number))
        {
            throw new ArgumentException($"Expected a {providerType} issue URL like {BuildRepositoryExample(providerType, server)}/issues/{{number}}.", nameof(issueUrl));
        }

        var repositoryName = NormalizeRepositoryName(parts[1]);
        var repositoryUrl = BuildRepositoryUrl(server, parts[0], repositoryName);
        return new DevOpsIssueReference(providerType, parts[0], repositoryName, number, $"{repositoryUrl}/issues/{number}", repositoryUrl, server);
    }

    public static DevOpsPullRequestReference ParsePullRequestUrl(
        DevOpsProviderType providerType,
        string pullRequestUrl,
        string? serverUrl = null)
    {
        var server = GetServerUrl(providerType, serverUrl);
        var uri = ParseAbsoluteUri(pullRequestUrl, nameof(pullRequestUrl));
        EnsureServer(providerType, uri, server, pullRequestUrl);

        var expectedSegment = providerType == DevOpsProviderType.GitHub ? "pull" : "pulls";
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4 || !parts[2].Equals(expectedSegment, StringComparison.OrdinalIgnoreCase) || !int.TryParse(parts[3], out var number))
        {
            throw new ArgumentException($"Expected a {providerType} pull request URL like {BuildRepositoryExample(providerType, server)}/{expectedSegment}/{{number}}.", nameof(pullRequestUrl));
        }

        var repositoryName = NormalizeRepositoryName(parts[1]);
        var repositoryUrl = BuildRepositoryUrl(server, parts[0], repositoryName);
        return new DevOpsPullRequestReference(providerType, parts[0], repositoryName, number, $"{repositoryUrl}/{expectedSegment}/{number}", repositoryUrl, server);
    }

    public static bool TryParseRepositoryUrl(
        DevOpsProviderType providerType,
        string repositoryUrl,
        string? serverUrl,
        out DevOpsRepositoryReference reference)
    {
        try
        {
            reference = ParseRepositoryUrl(providerType, repositoryUrl, serverUrl);
            return true;
        }
        catch (ArgumentException)
        {
            reference = null!;
            return false;
        }
    }

    public static Uri NormalizeServerUrl(DevOpsProviderType providerType, string? serverUrl)
        => GetServerUrl(providerType, serverUrl);

    private static Uri GetServerUrl(DevOpsProviderType providerType, string? serverUrl)
    {
        if (providerType == DevOpsProviderType.GitHub)
        {
            return GitHubServerUrl;
        }

        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw new ArgumentException("Gitea server URL is required.", nameof(serverUrl));
        }

        var uri = ParseAbsoluteUri(serverUrl, nameof(serverUrl));
        return new Uri(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/'));
    }

    private static Uri ParseAbsoluteUri(string value, string parameterName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Expected an absolute HTTP or HTTPS URL.", parameterName);
        }

        return uri;
    }

    private static void EnsureServer(DevOpsProviderType providerType, Uri uri, Uri server, string value)
    {
        if (!string.Equals(uri.Scheme, server.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, server.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != server.Port)
        {
            throw new ArgumentException($"URL '{value}' does not belong to the configured {providerType} server '{server}'.");
        }
    }

    private static string NormalizeRepositoryName(string value)
        => value.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;

    private static string BuildRepositoryUrl(Uri server, string owner, string name)
        => $"{server.ToString().TrimEnd('/')}/{owner}/{name}";

    private static string BuildRepositoryExample(DevOpsProviderType providerType, Uri server)
        => providerType == DevOpsProviderType.GitHub
            ? "https://github.com/{owner}/{repo}"
            : $"{server.ToString().TrimEnd('/')}/{{owner}}/{{repo}}";
}
