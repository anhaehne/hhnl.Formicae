export type StartWorkflowRequest = {
  issueUrl: string;
  repositoryUrl: string;
  baseBranch?: string | null;
  model?: string | null;
};

export type WorkflowSummary = {
  workflowId: string;
  issueUrl: string;
  repositoryUrl: string;
  status: string | number;
  currentStep: string | number;
  createdAt: string;
  updatedAt: string;
  pullRequestUrl?: string | null;
  failureReason?: string | null;
};

export type TaskRun = {
  id: string;
  workflowId: string;
  kind: string | number;
  status: string | number;
  externalId?: string | null;
  output?: string | null;
  failureReason?: string | null;
  startedAt?: string | null;
  completedAt?: string | null;
  createdAt: string;
  updatedAt: string;
  agentMessages: AgentMessage[];
};

export type AgentMessage = {
  sequence: number;
  role?: string | null;
  content: string;
  createdAt?: string | null;
};

export type WorkflowLog = {
  id: string;
  workflowId: string;
  taskRunId?: string | null;
  level: string;
  message: string;
  createdAt: string;
};

export type WorkflowEvent = {
  id: string;
  workflowId: string;
  taskRunId?: string | null;
  type: string;
  level: string;
  message: string;
  detailsJson?: string | null;
  createdAt: string;
};

export type WorkflowSignal = {
  severity: string;
  reason: string;
  workflowId: string;
  taskRunId?: string | null;
  observedAt: string;
};

export type WorkflowChatMessage = {
  id: string;
  author: string;
  body: string;
  url: string;
  updatedAt: string;
};

export type AiSettings = {
  id: string;
  name: string;
  provider?: string | null;
  model?: string | null;
  endpointUrl?: string | null;
  agentKind: string;
  acpProvider?: string | null;
  acpCommand?: string | null;
  authMethod: string;
  llmApiKeySecretName?: string | null;
  hasApiKeySecret: boolean;
  hasApiKey: boolean;
  apiKeyEnvironmentVariable?: string | null;
  hasSubscriptionAuth: boolean;
  subscriptionCredentialFileName?: string | null;
  subscriptionCredentialMountPath?: string | null;
  createdAt: string;
  updatedAt: string;
};

export type CodexAuthSetupStatus = {
  aiSettingsId: string;
  jobName: string;
  status: string;
  output: string;
  failureReason?: string | null;
  deviceLoginUrl?: string | null;
  deviceLoginCode?: string | null;
};

export type UpdateAiSettingsRequest = {
  id?: string | null;
  name?: string | null;
  provider?: string | null;
  model?: string | null;
  endpointUrl?: string | null;
  agentKind: string;
  acpProvider?: string | null;
  acpCommand?: string | null;
  authMethod: string;
  llmApiKeySecretName?: string | null;
  llmApiKey?: string | null;
  apiKeyEnvironmentVariable?: string | null;
  subscriptionCredentialJson?: string | null;
  subscriptionCredentialFileName?: string | null;
  subscriptionCredentialMountPath?: string | null;
  codexAuthJson?: string | null;
};

export type IntegrationSummary = {
  id: string;
  providerType: string;
  displayName: string;
  gitHubAppClientId: string;
  gitHubAppSlug?: string | null;
  webhookUrl: string;
  identityProviderEnabled: boolean;
  requiresRestart: boolean;
  createdAt: string;
  updatedAt: string;
};

export type GitHubSetupInstructions = {
  callbackUrl: string;
  installationCallbackUrl: string;
  installationUrl: string;
  webhookUrl: string;
  webhookSecret: string;
  requiredRepositoryPermissions: string[];
  requiredWebhookEvents: string[];
};

export type GitHubUserRepository = {
  owner: string;
  name: string;
  repositoryUrl: string;
  defaultBranch: string;
  private: boolean;
  installationId: number;
  installationAccount?: string | null;
};

export type ConnectedRepository = {
  id: string;
  owner: string;
  name: string;
  repositoryUrl: string;
  defaultBranch: string;
  installationId?: number | null;
  installationAccount?: string | null;
  createdAt: string;
  updatedAt: string;
};

export type IntegrationDetail = IntegrationSummary & {
  webhookSecret: string;
  capabilities: string[];
  setupInstructions: GitHubSetupInstructions;
  repositories: ConnectedRepository[];
};

export type CreateGitHubIntegrationRequest = {
  displayName: string;
  clientId: string;
  clientSecretReference?: string | null;
  privateKey: string;
  webhookSecret?: string | null;
};

export type AddConnectedRepositoryRequest = {
  repositoryUrl: string;
  defaultBranch?: string | null;
  installationId?: number | null;
  installationAccount?: string | null;
};

export type AppVersion = {
  version: string;
};

export type CurrentUser = {
  id?: string | null;
  authenticated: boolean;
  authorized: boolean;
  authRequired: boolean;
  canViewWorkflows: boolean;
  canTriggerWorkflows: boolean;
  canAdminister: boolean;
  name?: string | null;
  email?: string | null;
  provider?: string | null;
};

export type InviteCode = {
  id: string;
  createdAt: string;
  expiresAt: string;
  usedAt?: string | null;
  code?: string | null;
};

export type ManagementRole = {
  name: string;
  description: string;
  permissions: string[];
};

export type ManagementUser = {
  id: string;
  userName?: string | null;
  displayName?: string | null;
  email?: string | null;
  provider?: string | null;
  roles: string[];
  permissions: string[];
  createdAt: string;
  updatedAt: string;
  lastLoginAt?: string | null;
};

export type UpdateManagementUserRolesRequest = {
  roles: string[];
};
export async function getAppVersion(): Promise<AppVersion> {
  return send<AppVersion>("/api/version");
}

export async function getCurrentUser(): Promise<CurrentUser> {
  return send<CurrentUser>("/api/auth/current-user");
}

export async function createInvite(): Promise<InviteCode> {
  return send<InviteCode>("/api/auth/invites", { method: "POST" });
}

export async function redeemInvite(code: string): Promise<void> {
  await sendNoContent("/api/auth/invites/redeem", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ code })
  });
}

export async function listManagementRoles(): Promise<ManagementRole[]> {
  return send<ManagementRole[]>("/api/auth/roles");
}

export async function listManagementUsers(): Promise<ManagementUser[]> {
  return send<ManagementUser[]>("/api/auth/users");
}

export async function updateManagementUserRoles(userId: string, request: UpdateManagementUserRolesRequest): Promise<ManagementUser> {
  return send<ManagementUser>(`/api/auth/users/${encodeURIComponent(userId)}/roles`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request)
  });
}
export async function logout(): Promise<void> {
  await sendNoContent("/api/auth/logout", { method: "POST" });
}

export async function getAiSettings(): Promise<AiSettings[]> {
  return send<AiSettings[]>("/api/ai-settings");
}

export async function updateAiSettings(request: UpdateAiSettingsRequest): Promise<AiSettings> {
  return send<AiSettings>("/api/ai-settings", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request)
  });
}

export async function startCodexAuthConnection(settingsId: string): Promise<CodexAuthSetupStatus> {
  return send<CodexAuthSetupStatus>(`/api/ai-settings/${encodeURIComponent(settingsId)}/codex-auth/connect`, { method: "POST" });
}

export async function getCodexAuthConnectionStatus(settingsId: string, jobName: string): Promise<CodexAuthSetupStatus> {
  return send<CodexAuthSetupStatus>(`/api/ai-settings/${encodeURIComponent(settingsId)}/codex-auth/connect/${encodeURIComponent(jobName)}`);
}

export async function listIntegrations(): Promise<IntegrationSummary[]> {
  return send<IntegrationSummary[]>("/api/integrations");
}

export async function createGitHubIntegration(request: CreateGitHubIntegrationRequest): Promise<IntegrationDetail> {
  return send<IntegrationDetail>("/api/integrations/github", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request)
  });
}

export async function getIntegration(integrationId: string): Promise<IntegrationDetail> {
  return send<IntegrationDetail>(`/api/integrations/${encodeURIComponent(integrationId)}`);
}

export async function rotateWebhookSecret(integrationId: string): Promise<IntegrationDetail> {
  return send<IntegrationDetail>(`/api/integrations/${encodeURIComponent(integrationId)}/webhook-secret`, { method: "POST" });
}

export async function deleteIntegration(integrationId: string): Promise<void> {
  await sendNoContent(`/api/integrations/${encodeURIComponent(integrationId)}`, { method: "DELETE" });
}

export async function listGitHubUserRepositories(integrationId?: string): Promise<GitHubUserRepository[]> {
  const query = integrationId ? `?integrationId=${encodeURIComponent(integrationId)}` : "";
  return send<GitHubUserRepository[]>(`/api/auth/github/repositories${query}`);
}

export async function addConnectedRepository(integrationId: string, request: AddConnectedRepositoryRequest): Promise<ConnectedRepository> {
  return send<ConnectedRepository>(`/api/integrations/${encodeURIComponent(integrationId)}/repositories`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request)
  });
}

export async function deleteConnectedRepository(integrationId: string, repositoryId: string): Promise<void> {
  await sendNoContent(`/api/integrations/${encodeURIComponent(integrationId)}/repositories/${encodeURIComponent(repositoryId)}`, { method: "DELETE" });
}

export async function setIdentityProviderEnabled(integrationId: string, enabled: boolean): Promise<IntegrationDetail> {
  return send<IntegrationDetail>(`/api/integrations/${encodeURIComponent(integrationId)}/identity-provider`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ enabled })
  });
}

export async function restartIdentityProvider(integrationId: string): Promise<IntegrationDetail> {
  return send<IntegrationDetail>(`/api/integrations/${encodeURIComponent(integrationId)}/identity-provider/restart`, { method: "POST" });
}

export async function listWorkflows(limit = 25): Promise<WorkflowSummary[]> {
  return send<WorkflowSummary[]>(`/api/workflows?limit=${encodeURIComponent(limit)}`);
}

export async function startWorkflow(request: StartWorkflowRequest): Promise<WorkflowSummary> {
  return send<WorkflowSummary>("/api/workflows/github-issue", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request)
  });
}

export async function getWorkflow(workflowId: string): Promise<WorkflowSummary> {
  return send<WorkflowSummary>(`/api/workflows/${encodeURIComponent(workflowId)}`);
}

export async function listRuns(workflowId: string): Promise<TaskRun[]> {
  return send<TaskRun[]>(`/api/workflows/${encodeURIComponent(workflowId)}/runs`);
}

export async function retryTaskRun(workflowId: string, taskRunId: string): Promise<WorkflowSummary> {
  return send<WorkflowSummary>(`/api/workflows/${encodeURIComponent(workflowId)}/runs/${encodeURIComponent(taskRunId)}/retry`, { method: "POST" });
}

export async function retryWorkflow(workflowId: string): Promise<WorkflowSummary> {
  return send<WorkflowSummary>(`/api/workflows/${encodeURIComponent(workflowId)}/retry`, { method: "POST" });
}

export async function listLogs(workflowId: string): Promise<WorkflowLog[]> {
  return send<WorkflowLog[]>(`/api/workflows/${encodeURIComponent(workflowId)}/logs`);
}

export async function listEvents(workflowId: string): Promise<WorkflowEvent[]> {
  return send<WorkflowEvent[]>(`/api/workflows/${encodeURIComponent(workflowId)}/events`);
}

export async function listSignals(workflowId: string): Promise<WorkflowSignal[]> {
  return send<WorkflowSignal[]>(`/api/workflows/${encodeURIComponent(workflowId)}/signals`);
}

export async function listChatMessages(workflowId: string): Promise<WorkflowChatMessage[]> {
  return send<WorkflowChatMessage[]>(`/api/workflows/${encodeURIComponent(workflowId)}/chat-messages`);
}

async function send<T>(input: RequestInfo | URL, init?: RequestInit): Promise<T> {
  const response = await fetch(input, init);
  if (!response.ok) {
    const message = await readError(response);
    throw new Error(message);
  }

  return response.json() as Promise<T>;
}

async function sendNoContent(input: RequestInfo | URL, init?: RequestInit): Promise<void> {
  const response = await fetch(input, init);
  if (!response.ok) {
    const message = await readError(response);
    throw new Error(message);
  }
}

async function readError(response: Response): Promise<string> {
  const fallback = `${response.status} ${response.statusText}`;
  const text = await response.text();
  if (!text) {
    return fallback;
  }

  try {
    const payload = JSON.parse(text) as { error?: string };
    return payload.error ?? fallback;
  } catch {
    return text;
  }
}
