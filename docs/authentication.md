# Authentication

Formicae management authentication is opt-in. Local development, raw Kubernetes manifests, and Helm values keep `Auth:Enabled=false` by default, so existing anonymous workflows continue to work until protected mode is configured.

## Local Development

The default appsettings run anonymously:

```powershell
dotnet run --project src/hhnl.Formicae.Api/hhnl.Formicae.Api.csproj --urls http://localhost:5000
```

To test protected mode locally, create a GitHub OAuth app and set the callback URL to:

```text
http://localhost:5000/signin-github
```

Then configure user secrets or environment variables:

```powershell
dotnet user-secrets set "Auth:Enabled" "true" --project src/hhnl.Formicae.Api/hhnl.Formicae.Api.csproj
dotnet user-secrets set "Auth:GitHub:ClientId" "<client-id>" --project src/hhnl.Formicae.Api/hhnl.Formicae.Api.csproj
dotnet user-secrets set "Auth:GitHub:ClientSecret" "<client-secret>" --project src/hhnl.Formicae.Api/hhnl.Formicae.Api.csproj
dotnet user-secrets set "Auth:InviteCodes:0" "temporary-invite-code" --project src/hhnl.Formicae.Api/hhnl.Formicae.Api.csproj
```

Allowed users can also be configured directly:

```powershell
dotnet user-secrets set "Auth:AllowedGitHubLogins:0" "octocat" --project src/hhnl.Formicae.Api/hhnl.Formicae.Api.csproj
dotnet user-secrets set "Auth:AllowedEmails:0" "octocat@example.com" --project src/hhnl.Formicae.Api/hhnl.Formicae.Api.csproj
```

## GitHub OAuth App

Create an OAuth app in GitHub developer settings. Use the Formicae public URL as the homepage URL. The authorization callback URL must use the API host plus `/signin-github`, for example:

```text
https://formicae.example.com/signin-github
```

When `Auth:Enabled=true`, `Auth:GitHub:ClientId` and `Auth:GitHub:ClientSecret` are required at startup. When auth is disabled, empty OAuth settings are valid.

## Helm

Auth stays disabled unless `config.authEnabled` is set to `"true"`:

```powershell
helm upgrade --install formicae deploy/helm/formicae `
  --namespace formicae `
  --create-namespace `
  --set config.authEnabled=true `
  --set config.authGitHubClientId="<client-id>" `
  --set secrets.authGitHubClientSecret="<client-secret>" `
  --set secrets.authInviteCodes="temporary-invite-code" `
  --set config.authAllowedGitHubLogins="octocat"
```

The chart renders empty auth values by default. It only fails rendering for missing GitHub OAuth credentials when `config.authEnabled=true`.

## Raw Kubernetes

For raw manifests, set protected mode in `deploy/kubernetes/base/configmap.yaml`:

```yaml
Auth__Enabled: "true"
Auth__Provider: "GitHub"
Auth__CookieName: "formicae_auth"
Auth__GitHub__ClientId: "<client-id>"
Auth__AllowedGitHubLogins__0: "octocat"
Auth__AllowedEmails__0: ""
```

Set secrets in your runtime Secret:

```yaml
Auth__GitHub__ClientSecret: "<client-secret>"
Auth__InviteCodes__0: "temporary-invite-code"
```

Do not commit real OAuth secrets or invite codes.

## Invite Codes

Invite codes are configured statically for the MVP. Formicae hashes submitted invite codes before comparison and stores only the accepted invite hash with the GitHub identity.

Use short-lived, high-entropy invite codes. To rotate codes, add a new code, distribute it, then remove the old configured code after expected users have accepted. Previously accepted users remain allowed because their GitHub identity is persisted in `auth_users`.
