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
    public string Command { get; set; } = "openhands --headless --json --override-with-envs -t \"$FORMICAE_TASK_PROMPT\"";
    public string CodexSubscriptionImage { get; set; } = "mcr.microsoft.com/dotnet/sdk:10.0";
    public string CodexSubscriptionBootstrapCommand { get; set; } = "apt-get update && apt-get install -y --no-install-recommends git ca-certificates curl gnupg && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && apt-get install -y --no-install-recommends nodejs && rm -rf /var/lib/apt/lists/*";
    public string CodexSubscriptionCommand { get; set; } = "mkdir -p \"$CODEX_HOME\" /workspace && if [ -f /root/.codex/auth.json ]; then cp /root/.codex/auth.json \"$CODEX_HOME/auth.json\" && chmod 600 \"$CODEX_HOME/auth.json\"; fi && repo=\"${FORMICAE_REPOSITORY_URL#https://}\" && if [ \"$FORMICAE_TASK_KIND\" = \"Implement\" ] || [ \"$FORMICAE_TASK_KIND\" = \"AddressComments\" ]; then if [ -z \"$GITHUB_TOKEN\" ]; then echo \"GITHUB_TOKEN is required to push workflow changes.\" >&2; exit 1; fi; git clone \"https://x-access-token:${GITHUB_TOKEN}@${repo}\" /workspace/repo && cd /workspace/repo && git checkout \"$FORMICAE_BRANCH\" && git remote set-url origin \"https://x-access-token:${GITHUB_TOKEN}@${repo}\" && git config user.email \"formicae@example.invalid\" && git config user.name \"Formicae Agent\"; else cd /workspace; fi && codex_model_args=\"\" && if [ -n \"$FORMICAE_MODEL\" ]; then codex_model_args=\"-m $FORMICAE_MODEL\"; fi && npx -y @openai/codex exec $codex_model_args -C \"$PWD\" --skip-git-repo-check --json --dangerously-bypass-approvals-and-sandbox \"$FORMICAE_TASK_PROMPT\" && if [ \"$FORMICAE_TASK_KIND\" = \"Implement\" ] || [ \"$FORMICAE_TASK_KIND\" = \"AddressComments\" ]; then git remote set-url origin \"https://x-access-token:${GITHUB_TOKEN}@${repo}\" && git add -A && if git diff --cached --quiet; then echo \"Codex completed without uncommitted file changes.\"; else commit_subject=\"Implement Formicae workflow ${FORMICAE_WORKFLOW_ID}\" && if [ \"$FORMICAE_TASK_KIND\" = \"AddressComments\" ]; then commit_subject=\"Address comments for Formicae workflow ${FORMICAE_WORKFLOW_ID}\"; fi && git commit -m \"$commit_subject\"; fi && git push origin \"$FORMICAE_BRANCH\"; fi";
}

public static class OpenHandsAuthMethods
{
    public const string ApiKey = "ApiKey";
    public const string CodexSubscription = "CodexSubscription";
}
