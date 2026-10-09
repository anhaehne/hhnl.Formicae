export type StartWorkflowRequest = {
  issueUrl: string;
  repositoryUrl: string;
  baseBranch?: string | null;
  model?: string | null;
  workflowDefinitionId?: string | null;
  workflowDefinitionVersionId?: string | null;
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
  currentDefinitionStepId?: string | null;
  isPaused?: boolean;
  cancelRequestedAt?: string | null;
  cancelCompletedAt?: string | null;
  workflowDefinitionId?: string | null;
  workflowDefinitionVersionId?: string | null;
};

export type WorkflowDefinitionDocument = {
  schema: string;
  defaultPersonaId?: string | null;
  defaultEnvironmentId?: string | null;
  defaultEnvironmentSnapshot?: EnvironmentSnapshot | null;
  startStepId: string;
  steps: WorkflowDefinitionStep[];
  variables?: WorkflowDataVariable[] | null;
  triggers?: WorkflowDefinitionTrigger[] | null;
  loops?: WorkflowDefinitionLoop[] | null;
  editor?: { groups?: WorkflowEditorGroup[] | null; positions: Record<string, { x: number; y: number }>; viewport?: { x: number; y: number; zoom: number } | null } | null;
};

export type WorkflowDefinitionLoop = {
  id: string;
  bodyStepIds: string[];
  repeatCount: number;
  maxIterations: number;
  timeoutSeconds?: number | null;
  exitStepId: string;
};

export type WorkflowWaitSettings = { repositoryId?: string | null; issueNumber?: number | null; issueNumberBinding?: { stepId: string; outputName: string } | null };
export type WorkflowEventSettings = { enabled: boolean; repositoryIds?: string[]; label?: string | null; baseBranch?: string | null; model?: string | null; webhookSecretName?: string | null; [key: string]: unknown };
export type WorkflowIssueCommentSettings = { inputs?: Record<string, string | number | boolean>; bindings?: Record<string, CustomTaskInputBinding> };
export type WorkflowEventDescriptor = { outputs?: CustomTaskOutputDefinition[] | null; uses: string; title: string; description: string; provider?: string | null; manual: boolean; webhook: boolean; legacy: boolean; callable?: boolean; fields: { name: string; label: string; kind: string; required: boolean }[] };
export const listWorkflowEventDefinitions = () => send<WorkflowEventDescriptor[]>("/api/workflow-events");

export type WorkflowTriggerType = "Manual" | "DevOpsIssueLabel" | "Webhook" | "DevOpsIssueCreated";

export type WorkflowDefinitionTrigger = {
  id: string;
  type: WorkflowTriggerType;
  enabled: boolean;
  repositoryIds: string[];
  label?: string | null;
  baseBranch?: string | null;
  model?: string | null;
};

export type WorkflowTriggerNodeSettings = Omit<WorkflowDefinitionTrigger, "id"> & { webhookSecretName?: string | null };
export type DecisionCondition = {
  source: "literal" | "workflowField" | "taskOutput";
  valueType: "string" | "number" | "boolean";
  operator: "equals" | "notEquals" | "contains" | "exists" | "greaterThan" | "greaterThanOrEqual" | "lessThan" | "lessThanOrEqual";
  reference?: string | null; value?: string | number | boolean | null; compareTo?: string | number | boolean | null;
  missingValue: "error" | "false";
};
export type WorkflowDecisionNodeSettings = { condition: DecisionCondition; trueStepId: string; falseStepId: string };
export type WorkflowDecisionExecution = { id: string; workflowId: string; nodeId: string; booleanResult: boolean; configuredTargetId: string; selectedTargetId: string; evaluatedAt: string; inputJson: string; sourceTaskRunId?: string | null };
export type WorkflowParallelNodeSettings = { branchStepIds: string[] };

export type WorkflowLoopNodeSettings = { bodyStepId: string; repeatCount: number; maxIterations: number; timeoutSeconds?: number | null };

export type WorkflowDefinitionStep = {
  id: string;
  uses: string;
  nextStepId?: string | null;
  nextStepIds?: string[] | null;
  displayName?: string | null;
  aiSettingsId?: string | null;
  model?: string | null;
  personaId?: string | null;
  personaSnapshot?: PersonaSnapshot | null;
  imageSelection?: ImageSelection | null;
  imageSnapshot?: PreparedImageSnapshot | null;
  environmentId?: string | null;
  environmentSnapshot?: EnvironmentSnapshot | null;
  customTask?: WorkflowCustomTaskSettings | null;
  script?: WorkflowScriptSettings | null; issueComment?: WorkflowIssueCommentSettings | null;
  capabilities?: string[] | null;
  secretReferences?: StepSecretReference[] | null;
  trigger?: WorkflowTriggerNodeSettings | null;
  event?: WorkflowEventSettings | null;
  wait?: WorkflowWaitSettings | null;
  loop?: WorkflowLoopNodeSettings | null;
  parallel?: WorkflowParallelNodeSettings | null;
  decision?: WorkflowDecisionNodeSettings | null;
  nextStepPort?: "return" | "join" | null;
};

export type ModelDiscoveryStatus = {
  aiSettingsId: string;
  jobName?: string | null;
  status: "Running" | "Succeeded" | "Failed" | "Unsupported";
  models: Array<{ id: string; displayName: string; isDefault: boolean }>;
  failureReason?: string | null;
};

export function startModelDiscovery(settingsId: string) {
  return send<ModelDiscoveryStatus>(`/api/ai-settings/${encodeURIComponent(settingsId)}/models/discover`, { method: "POST" });
}

export function getModelDiscovery(settingsId: string, jobName: string) {
  return send<ModelDiscoveryStatus>(`/api/ai-settings/${encodeURIComponent(settingsId)}/models/discover/${encodeURIComponent(jobName)}`);
}

export type WorkflowDefinitionValidationError = {
  code: string;
  message: string;
  path?: string | null;
  nodeId?: string | null;
  connectionId?: string | null;
};

export type WorkflowDefinitionResponse = {
  id: string;
  name: string;
  createdAt: string;
  updatedAt: string;
  versions: WorkflowDefinitionVersionResponse[];
};

export type WorkflowDefinitionVersionResponse = {
  id: string;
  workflowDefinitionId: string;
  version: number;
  dslSchemaVersion: string;
  isEnabled: boolean;
  isDefault: boolean;
  definition: WorkflowDefinitionDocument;
  createdAt: string;
};

export type CreateWorkflowDefinitionRequest = {
  name: string;
};

export type CreateWorkflowDefinitionVersionRequest = {
  version?: number | null;
  isEnabled: boolean;
  isDefault: boolean;
  definition: WorkflowDefinitionDocument;
};

export class ApiError extends Error {
  readonly status: number;
  readonly validationErrors: WorkflowDefinitionValidationError[];

  constructor(message: string, status: number, validationErrors: WorkflowDefinitionValidationError[] = []) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.validationErrors = validationErrors;
  }
}

export type PreparedIssueCommentExecution = { inputs: Record<string, string | number | boolean>; provenance: Record<string, CustomTaskInputProvenance> };
export type TaskRun = {
  issueCommentExecution?: PreparedIssueCommentExecution | null;
  id: string;
  workflowId: string;
  kind: string | number;
  status: string | number;
  externalId?: string | null;
  output?: string | null;
  customTaskExecution?: PreparedCustomTaskExecution | null;
  structuredOutputs?: Record<string, CustomTaskScalar> | null;
  failureReason?: string | null;
  exitCode?: number | null;
  startedAt?: string | null;
  completedAt?: string | null;
  createdAt: string;
  updatedAt: string;
  agentMessages: AgentMessage[];
  definitionStepId: string;
  loopIteration?: number | null;
  executionAttemptId?: string | null;
  attemptCount?: number;
};

export type WorkflowLoopIteration = {
  id: string;
  workflowId: string;
  loopId: string;
  iterationNumber: number;
  startedAt: string;
  completedAt?: string | null;
  outcome: string | number;
  failureReason?: string | null;
};

export type AgentMessage = {
  sequence: number;
  role?: string | null;
  content: string;
  createdAt?: string | null;
};

export type WorkflowLog = {
  sequence: number;
  executionAttemptId?: string | null;
  source?: string | null;
  sourceSequence?: number | null;
  externalId?: string | null;
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
  serverUrl?: string | null;
  webhookUrl: string;
  identityProviderEnabled: boolean;
  requiresRestart: boolean;
  createdAt: string;
  updatedAt: string;
};

export type DevOpsSetupInstructions = {
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
  setupInstructions: DevOpsSetupInstructions;
  repositories: ConnectedRepository[];
};

export type CreateGitHubIntegrationRequest = {
  displayName: string;
  clientId: string;
  clientSecretReference?: string | null;
  privateKey: string;
  webhookSecret?: string | null;
};

export type CreateGiteaIntegrationRequest = {
  displayName: string;
  serverUrl: string;
  accessToken: string;
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

export async function createGiteaIntegration(request: CreateGiteaIntegrationRequest): Promise<IntegrationDetail> {
  return send<IntegrationDetail>("/api/integrations/gitea", {
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

export async function listWorkflowDefinitions(): Promise<WorkflowDefinitionResponse[]> {
  return send<WorkflowDefinitionResponse[]>("/api/workflow-definitions");
}

export async function getWorkflowDefinition(definitionId: string): Promise<WorkflowDefinitionResponse> {
  return send<WorkflowDefinitionResponse>(`/api/workflow-definitions/${encodeURIComponent(definitionId)}`);
}

export async function createWorkflowDefinition(request: CreateWorkflowDefinitionRequest): Promise<WorkflowDefinitionResponse> {
  return send<WorkflowDefinitionResponse>("/api/workflow-definitions", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request)
  });
}

export async function createWorkflowDefinitionVersion(definitionId: string, request: CreateWorkflowDefinitionVersionRequest): Promise<WorkflowDefinitionVersionResponse> {
  return send<WorkflowDefinitionVersionResponse>(`/api/workflow-definitions/${encodeURIComponent(definitionId)}/versions`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request)
  });
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

export async function listLoopIterations(workflowId: string): Promise<WorkflowLoopIteration[]> {
  return send<WorkflowLoopIteration[]>(`/api/workflows/${encodeURIComponent(workflowId)}/loop-iterations`);
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

export async function listEvents(workflowId: string, signal?: AbortSignal): Promise<WorkflowEvent[]> {
  return send<WorkflowEvent[]>(`/api/workflows/${encodeURIComponent(workflowId)}/events`, { signal });
}

export async function listSignals(workflowId: string, signal?: AbortSignal): Promise<WorkflowSignal[]> {
  return send<WorkflowSignal[]>(`/api/workflows/${encodeURIComponent(workflowId)}/signals`, { signal });
}

export async function listChatMessages(workflowId: string, signal?: AbortSignal): Promise<WorkflowChatMessage[]> {
  return send<WorkflowChatMessage[]>(`/api/workflows/${encodeURIComponent(workflowId)}/chat-messages`, { signal });
}

async function send<T>(input: RequestInfo | URL, init?: RequestInit): Promise<T> {
  const response = await fetch(input, init);
  if (!response.ok) {
    throw await readError(response);
  }

  return response.json() as Promise<T>;
}

async function sendNoContent(input: RequestInfo | URL, init?: RequestInit): Promise<void> {
  const response = await fetch(input, init);
  if (!response.ok) {
    throw await readError(response);
  }
}

async function readError(response: Response): Promise<ApiError> {
  const fallback = `${response.status} ${response.statusText}`;
  const text = await response.text();
  if (!text) {
    return new ApiError(fallback, response.status);
  }

  try {
    const payload = JSON.parse(text) as { error?: string; errors?: WorkflowDefinitionValidationError[] };
    if (Array.isArray(payload.errors) && payload.errors.length > 0) {
      return new ApiError(formatValidationErrors(payload.errors), response.status, payload.errors);
    }

    return new ApiError(payload.error ?? fallback, response.status);
  } catch {
    return new ApiError(text, response.status);
  }
}

function formatValidationErrors(errors: WorkflowDefinitionValidationError[]) {
  return errors
    .map(error => error.path ? `${error.path}: ${error.message}` : error.message)
    .join("\n");
}

export function validateWorkflowDefinition(definition: WorkflowDefinitionDocument) {
  return send<{ isValid: boolean; errors: WorkflowDefinitionValidationError[] }>("/api/workflow-definitions/validate", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(definition) });
}

export async function listDecisionExecutions(workflowId: string): Promise<WorkflowDecisionExecution[]> {
  return send<WorkflowDecisionExecution[]>(`/api/workflows/${encodeURIComponent(workflowId)}/decisions`);
}

export type PersonaSnapshot = { id: string; revision: number; name: string; instructions: string; tone: string; operatingConstraints: string };
export type Persona = PersonaSnapshot & { builtIn: boolean; createdAt: string; updatedAt: string };
export type PersonaInput = Pick<Persona, "name" | "instructions" | "tone" | "operatingConstraints">;
export const listPersonas = () => send<Persona[]>("/api/personas");
export const getPersona = (id: string) => send<Persona>(`/api/personas/${encodeURIComponent(id)}`);
export const createPersona = (input: PersonaInput) => send<Persona>("/api/personas", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(input) });
export const updatePersona = (id: string, input: PersonaInput, expectedRevision: number) => send<Persona>(`/api/personas/${encodeURIComponent(id)}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ ...input, expectedRevision }) });
export const deletePersona = (id: string, expectedRevision: number) => sendNoContent(`/api/personas/${encodeURIComponent(id)}?expectedRevision=${expectedRevision}`, { method: "DELETE" });

export type CustomTaskScalar = string | number | boolean;
export type CustomTaskInputDefinition = { name: string; valueType: "string" | "number" | "boolean"; required: boolean; defaultValue?: CustomTaskScalar | null };
export type CustomTaskOutputDefinition = Omit<CustomTaskInputDefinition, "defaultValue">;
export type WorkflowDataVariable = { id: string; name: string; valueType: "string" | "number" | "boolean"; mode: "aggregate" | "first" | "override"; sources: CustomTaskInputBinding[]; separator: string; booleanOperation: "any" | "all" };
export type CustomTaskInputBinding = { stepId: string; outputName: string };
export type CustomTaskInputProvenance = CustomTaskInputBinding & { runId: string; executionAttemptId: string; loopIteration?: number | null; value?: CustomTaskScalar | null; variable?: { configuration: WorkflowDataVariable; sources: CustomTaskInputProvenance[] } | null };
export type CustomTaskRunnerSettings = { kind: "agent"; timeoutSeconds: number };
export type CustomTaskSnapshot = { id: string; revision: number; name: string; description: string; promptTemplate: string; inputs: CustomTaskInputDefinition[]; outputs?: CustomTaskOutputDefinition[]; runner: CustomTaskRunnerSettings };
export type CustomTaskDefinition = CustomTaskSnapshot & { createdAt: string; updatedAt: string };
export type CustomTaskInput = Omit<CustomTaskSnapshot, "id" | "revision">;
export type AgentTaskDefinition = { promptTemplate: string; inputs: CustomTaskInputDefinition[]; runner: CustomTaskRunnerSettings; outputs?: CustomTaskOutputDefinition[] };
export type WorkflowCustomTaskSettings = { taskId: string; definition?: AgentTaskDefinition | null; bindings?: Record<string, CustomTaskInputBinding> | null; inputs?: Record<string, CustomTaskScalar> | null; snapshot?: CustomTaskSnapshot | null };
export type PreparedCustomTaskExecution = { taskId: string; revision: number; name: string; inputs: Record<string, CustomTaskScalar>; workflowFields: Record<string, CustomTaskScalar | null>; timeoutSeconds: number; prompt: string; formatVersion: number; provenance?: Record<string, CustomTaskInputProvenance> | null };
export const listCustomTasks = () => send<CustomTaskDefinition[]>("/api/custom-tasks");
export const createCustomTask = (input: CustomTaskInput) => send<CustomTaskDefinition>("/api/custom-tasks", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(input) });
export const updateCustomTask = (id: string, input: CustomTaskInput, expectedRevision: number) => send<CustomTaskDefinition>(`/api/custom-tasks/${encodeURIComponent(id)}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ ...input, expectedRevision }) });
export const deleteCustomTask = (id: string, expectedRevision: number) => sendNoContent(`/api/custom-tasks/${encodeURIComponent(id)}?expectedRevision=${expectedRevision}`, { method: "DELETE" });

export type StepSecretReference = { environmentName: string; secretName: string; key: string };
export type WorkflowScriptSettings = { shell: "sh" | "bash"; script: string; timeoutSeconds: number; workingDirectory: "workspace" | "repository" };
export type EnvironmentImage = { reference: string; pullPolicy: "Always" | "IfNotPresent" | "Never"; pullSecretNames?: string[] | null };
export type EnvironmentTool = { name: string; script: string; shell: "sh" | "bash"; timeoutSeconds: number };
export type EnvironmentMcpServer = { name: string; transport: "stdio" | "http"; command?: string | null; arguments?: string[] | null; url?: string | null; environmentVariables?: Record<string, string> | null; bearerTokenEnvironmentVariable?: string | null; headerEnvironmentVariables?: Record<string, string> | null };
export type EnvironmentConfiguration = { schemaVersion: number; runtime?: { timeoutLimitSeconds?: number | null } | null; image?: EnvironmentImage | null; imageSelection?: ImageSelection | null; imageSnapshot?: PreparedImageSnapshot | null; tools: EnvironmentTool[]; mcpServers: EnvironmentMcpServer[] };
export type EnvironmentSnapshot = { id: string; revision: number; name: string; description: string; configuration: EnvironmentConfiguration };
export type EnvironmentProfile = EnvironmentSnapshot & { builtIn: boolean; createdAt: string; updatedAt: string };
export type EnvironmentInput = Pick<EnvironmentSnapshot, "name" | "description" | "configuration">;
export const listEnvironments = () => send<EnvironmentProfile[]>("/api/environments");
export const createEnvironment = (input: EnvironmentInput) => send<EnvironmentProfile>("/api/environments", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(input) });
export const updateEnvironment = (id: string, input: EnvironmentInput, expectedRevision: number) => send<EnvironmentProfile>(`/api/environments/${encodeURIComponent(id)}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ ...input, expectedRevision }) });
export const deleteEnvironment = (id: string, expectedRevision: number) => sendNoContent(`/api/environments/${encodeURIComponent(id)}?expectedRevision=${expectedRevision}`, { method: "DELETE" });

export type TaskRunAttempt = {
 id: string; workflowId: string; taskRunId: string; executionAttemptId: string; attemptNumber: number;
 status: string | number; externalId?: string | null; output?: string | null; failureReason?: string | null; exitCode?: number | null;
 structuredOutputsJson?: string | null; customTaskExecutionJson?: string | null; definitionStepId: string;
 loopIteration?: number | null; startedAt?: string | null; completedAt?: string | null; createdAt: string; updatedAt: string;
};
export type WorkflowExecution = {
 workflow: WorkflowSummary; definitionVersionId?: string | null; definition: WorkflowDefinitionDocument | null;
 waits?: Array<{ id: string; workflowId: string; taskRunId: string; executionAttemptId: string; issueUrl: string; armedAt: string; matchedAt?: string | null; matchedEventId?: string | null; isCanceled: boolean; inputProvenanceJson?: string | null }>;
 runs: TaskRun[]; attempts: TaskRunAttempt[]; loops: WorkflowLoopIteration[]; decisions: WorkflowDecisionExecution[];
 parallels?: Array<{ id: string; workflowId: string; nodeId: string; outcome: string; startedAt: string; completedAt?: string | null }>;
 resolvedSettings?: Array<{ taskRunId: string; executionAttemptId?: string | null; aiSettingsId?: string | null; model?: string | null; personaId?: string | null; personaRevision?: number | null; personaName?: string | null; capabilities?: string[] | null; secretReferences?: StepSecretReference[] | null; environment?: unknown }>;
 control: { isPaused: boolean; cancelRequestedAt?: string | null; cancelCompletedAt?: string | null; canPause: boolean; canResume: boolean; canCancel: boolean };
};
export type WorkflowFilters = { active?: string; search?: string; status?: string; repositoryUrl?: string; definitionId?: string; from?: string; to?: string };
export type WorkflowSearchPage = { items: WorkflowSummary[]; totalCount: number; offset: number; limit: number };
export type LogFilters = { taskRunId?: string; executionAttemptId?: string; level?: string; source?: string; search?: string };
export type WorkflowLogPage = { items: WorkflowLog[]; nextCursor: number; previousCursor: number; hasMore: boolean; hasEarlier: boolean };
function executionQuery(values: Record<string, string | number | undefined>) {
 const query = new URLSearchParams();
 for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== "") query.set(key, String(value));
 return query.toString();
}
export function searchWorkflows(filters: WorkflowFilters, offset = 0, limit = 25, signal?: AbortSignal) {
 return send<WorkflowSearchPage>(`/api/workflows/search?${executionQuery({ ...filters, offset, limit })}`, { signal });
}
export function getWorkflowExecution(workflowId: string, signal?: AbortSignal) {
 return send<WorkflowExecution>(`/api/workflows/${encodeURIComponent(workflowId)}/execution`, { signal });
}
export function getWorkflowLogPage(workflowId: string, filters: LogFilters, cursor: { after?: number; before?: number } = {}, signal?: AbortSignal) {
 return send<WorkflowLogPage>(`/api/workflows/${encodeURIComponent(workflowId)}/logs/page?${executionQuery({ ...filters, ...cursor, limit: 200 })}`, { signal });
}
export function workflowLogUrl(workflowId: string, operation: "stream" | "download", filters: LogFilters, after?: number) {
 return `/api/workflows/${encodeURIComponent(workflowId)}/logs/${operation}?${executionQuery({ ...filters, after })}`;
}
export function controlWorkflow(workflowId: string, action: "pause" | "resume" | "cancel") {
 return send<WorkflowSummary>(`/api/workflows/${encodeURIComponent(workflowId)}/${action}`, { method: "POST" });
}
export function workflowEvidenceUrl(workflowId: string) { return `/api/workflows/${encodeURIComponent(workflowId)}/evidence`; }

export type ImageSource = { dockerfile: string; repositoryUrl?: string | null; ref: string; dockerfilePath: string; contextPath: string; target?: string | null; buildArguments?: Record<string, string> | null; platform: string };
export type ManagedImage = { id: string; name: string; description: string; revision: number; source: ImageSource; isArchived: boolean; createdAt: string; updatedAt: string };
export type ImageBuild = { id: string; imageId: string; imageName: string; sourceRevision: number; state: string; commitSha?: string | null; reference?: string | null; failure?: string | null; logs: string; createdAt: string; updatedAt: string };
export type ImageSelection = { mode: "inherit" | "platform" | "managed"; imageId?: string | null; buildId?: string | null };
export type PreparedImageSnapshot = { imageId: string; buildId: string; sourceRevision: number; name: string; reference: string; pullPolicy: "IfNotPresent"; pullSecretNames: string[]; workerProtocolVersion: number; platform: string };
export type ManagedImageInput = Pick<ManagedImage, "name" | "description" | "source">;
export type ImageConfiguration = { enabled: boolean; developmentAdapter: boolean; workerBaseImage: string; pullSecretNames: string[] };
export const imageConfiguration = () => send<ImageConfiguration>("/api/images/configuration");
export const listImages = () => send<ManagedImage[]>("/api/images");
export const createImage = (input: ManagedImageInput) => send<ManagedImage>("/api/images", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(input) });
export const updateImage = (id: string, input: ManagedImageInput, expectedRevision: number) => send<ManagedImage>(`/api/images/${encodeURIComponent(id)}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ ...input, expectedRevision }) });
export const archiveImage = (id: string, expectedRevision: number) => sendNoContent(`/api/images/${encodeURIComponent(id)}?expectedRevision=${expectedRevision}`, { method: "DELETE" });
export const listImageBuilds = (id: string) => send<ImageBuild[]>(`/api/images/${encodeURIComponent(id)}/builds`);
export const queueImageBuild = (id: string, expectedRevision: number) => send<ImageBuild>(`/api/images/${encodeURIComponent(id)}/builds`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ expectedRevision }) });
export const cancelImageBuild = (id: string, buildId: string) => send<ImageBuild>(`/api/images/${encodeURIComponent(id)}/builds/${encodeURIComponent(buildId)}/cancel`, { method: "POST" });

export type WorkflowEditorGroup = { id: string; name: string; color: string; nodeIds: string[] };
