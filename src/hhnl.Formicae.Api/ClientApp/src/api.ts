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
  provider?: string | null;
  model?: string | null;
  endpointUrl?: string | null;
  authMethod: string;
  llmApiKeySecretName?: string | null;
  hasApiKeySecret: boolean;
};

export type UpdateAiSettingsRequest = {
  provider?: string | null;
  model?: string | null;
  endpointUrl?: string | null;
  authMethod: string;
  llmApiKeySecretName?: string | null;
};

export type IntegrationSummary = {
  id: string;
  providerType: string;
  displayName: string;
  gitHubAppClientId: string;
  webhookUrl: string;
  identityProviderEnabled: boolean;
  requiresRestart: boolean;
  createdAt: string;
  updatedAt: string;
};

export type GitHubSetupInstructions = {
  callbackUrl: string;
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
  clientSecretReference: string;
  webhookSecret?: string | null;
};

export type AddConnectedRepositoryRequest = {
  repositoryUrl: string;
  defaultBranch?: string | null;
  installationId?: number | null;
  installationAccount?: string | null;
};

export async function getAiSettings(): Promise<AiSettings> {
  return send<AiSettings>("/api/ai-settings");
}

export async function updateAiSettings(request: UpdateAiSettingsRequest): Promise<AiSettings> {
  return send<AiSettings>("/api/ai-settings", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request)
  });
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

export async function listGitHubUserRepositories(): Promise<GitHubUserRepository[]> {
  return send<GitHubUserRepository[]>("/api/auth/github/repositories");
}

export async function addConnectedRepository(integrationId: string, request: AddConnectedRepositoryRequest): Promise<ConnectedRepository> {
  return send<ConnectedRepository>(`/api/integrations/${encodeURIComponent(integrationId)}/repositories`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request)
  });
}

export async function setIdentityProviderEnabled(integrationId: string, enabled: boolean): Promise<IntegrationDetail> {
  return send<IntegrationDetail>(`/api/integrations/${encodeURIComponent(integrationId)}/identity-provider`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ enabled })
  });
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
