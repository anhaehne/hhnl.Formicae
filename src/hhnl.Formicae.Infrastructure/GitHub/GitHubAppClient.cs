using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using hhnl.Formicae.Application.Integrations;
using Octokit;

namespace hhnl.Formicae.Infrastructure.GitHub;

public sealed class GitHubAppClient : IGitHubAppClient
{
    private static readonly ProductHeaderValue ProductHeader = new("hhnl-formicae");

    public async Task<GitHubAppMetadata> GetAppMetadataAsync(DevOpsIntegration integration, CancellationToken cancellationToken)
    {
        var appClient = CreateAppClient(integration);
        var app = await appClient.GitHubApps.GetCurrent();
        return new GitHubAppMetadata(app.Slug, app.HtmlUrl);
    }

    public async Task<IReadOnlyList<GitHubInstallationRepository>> ListInstallationRepositoriesAsync(DevOpsIntegration integration, CancellationToken cancellationToken)
    {
        var appClient = CreateAppClient(integration);
        var installations = await appClient.GitHubApps.GetAllInstallationsForCurrent();
        var repositories = new List<GitHubInstallationRepository>();

        foreach (var installation in installations)
        {
            var installationToken = await CreateInstallationTokenAsync(appClient, installation.Id);
            var installationClient = CreateInstallationClient(installationToken);
            var response = await installationClient.GitHubApps.Installation.GetAllRepositoriesForCurrent();
            repositories.AddRange(response.Repositories.Select(repository => new GitHubInstallationRepository(
                repository.Owner.Login,
                repository.Name,
                repository.HtmlUrl,
                repository.DefaultBranch,
                repository.Private,
                installation.Id,
                installation.Account?.Login)));
        }

        return repositories
            .OrderBy(repository => repository.Owner, StringComparer.OrdinalIgnoreCase)
            .ThenBy(repository => repository.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<string> CreateInstallationTokenAsync(DevOpsIntegration integration, long installationId, CancellationToken cancellationToken)
    {
        var appClient = CreateAppClient(integration);
        return await CreateInstallationTokenAsync(appClient, installationId);
    }

    private static async Task<string> CreateInstallationTokenAsync(GitHubClient appClient, long installationId)
    {
        var token = await appClient.GitHubApps.CreateInstallationToken(installationId);
        return token.Token;
    }

    private static GitHubClient CreateAppClient(DevOpsIntegration integration)
        => new(ProductHeader)
        {
            Credentials = new Credentials(CreateJwt(integration), AuthenticationType.Bearer)
        };

    private static GitHubClient CreateInstallationClient(string installationToken)
        => new(ProductHeader)
        {
            Credentials = new Credentials(installationToken)
        };

    private static string CreateJwt(DevOpsIntegration integration)
    {
        if (string.IsNullOrWhiteSpace(integration.GitHubAppClientId))
        {
            throw new InvalidOperationException("GitHub App client id is required.");
        }

        var privateKey = DevOpsIntegrationService.NormalizePrivateKey(integration.GitHubAppPrivateKey);
        var now = DateTimeOffset.UtcNow;
        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT"
        }));
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iat"] = now.AddSeconds(-60).ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(9).ToUnixTimeSeconds(),
            ["iss"] = integration.GitHubAppClientId
        }));
        var signingInput = $"{header}.{payload}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKey);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}