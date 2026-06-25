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
  createdAt: string;
  updatedAt: string;
};

export type WorkflowLog = {
  id: string;
  workflowId: string;
  taskRunId?: string | null;
  level: string;
  message: string;
  createdAt: string;
};

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
