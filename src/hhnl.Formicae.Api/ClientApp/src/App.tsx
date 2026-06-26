import { FormEvent, useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { Dispatch, ReactNode, SetStateAction } from "react";
import {
  addConnectedRepository,
  AiSettings,
  ConnectedRepository,
  createGitHubIntegration,
  getAiSettings,
  getIntegration,
  getWorkflow,
  IntegrationDetail,
  IntegrationSummary,
  listChatMessages,
  listEvents,
  listIntegrations,
  listLogs,
  listRuns,
  listSignals,
  listWorkflows,
  rotateWebhookSecret,
  setIdentityProviderEnabled,
  startWorkflow,
  TaskRun,
  updateAiSettings,
  WorkflowChatMessage,
  WorkflowEvent,
  WorkflowLog,
  WorkflowSignal,
  WorkflowSummary
} from "./api";

const workflowStatuses = ["Queued", "Planning", "Implementing", "CreatingPullRequest", "Reviewing", "Completed", "Failed", "Canceled"];
const workflowSteps = ["None", "Plan", "Implement", "CreatePullRequest", "AddressComments", "Done"];
const taskRunKinds = ["Plan", "Implement", "CreatePullRequest", "AddressComments"];
const taskRunStatuses = ["Queued", "Running", "Succeeded", "Failed"];

type FormState = {
  issueUrl: string;
  repositoryUrl: string;
  baseBranch: string;
  model: string;
};

type AiSettingsFormState = {
  provider: string;
  model: string;
  authMethod: string;
  llmApiKeySecretName: string;
  endpointUrl: string;
};

type Page = "workflows" | "integrations" | "settings";

type GitHubIntegrationFormState = {
  displayName: string;
  clientId: string;
  clientSecretReference: string;
};

type RepositoryFormState = {
  repositoryUrl: string;
  defaultBranch: string;
  installationId: string;
  installationAccount: string;
};

type DetailState = {
  workflow?: WorkflowSummary;
  runs: TaskRun[];
  logs: WorkflowLog[];
  events: WorkflowEvent[];
  signals: WorkflowSignal[];
  chatMessages: WorkflowChatMessage[];
  loading: boolean;
  error?: string;
};

const initialForm: FormState = {
  issueUrl: "",
  repositoryUrl: "",
  baseBranch: "main",
  model: ""
};

const initialAiSettingsForm: AiSettingsFormState = {
  provider: "",
  model: "",
  authMethod: "ApiKey",
  llmApiKeySecretName: "",
  endpointUrl: ""
};

const initialGitHubIntegrationForm: GitHubIntegrationFormState = {
  displayName: "GitHub",
  clientId: "",
  clientSecretReference: ""
};

const initialRepositoryForm: RepositoryFormState = {
  repositoryUrl: "",
  defaultBranch: "main",
  installationId: "",
  installationAccount: ""
};

export default function App() {
  const [activePage, setActivePage] = useState<Page>("workflows");
  const [form, setForm] = useState<FormState>(initialForm);
  const modelTouched = useRef(false);
  const [aiSettings, setAiSettings] = useState<AiSettings>();
  const [aiSettingsForm, setAiSettingsForm] = useState<AiSettingsFormState>(initialAiSettingsForm);
  const [loadingAiSettings, setLoadingAiSettings] = useState(false);
  const [savingAiSettings, setSavingAiSettings] = useState(false);
  const [aiSettingsError, setAiSettingsError] = useState<string>();
  const [aiSettingsSaved, setAiSettingsSaved] = useState<string>();
  const [workflows, setWorkflows] = useState<WorkflowSummary[]>([]);
  const [selectedWorkflowId, setSelectedWorkflowId] = useState<string>();
  const [detail, setDetail] = useState<DetailState>({ runs: [], logs: [], events: [], signals: [], chatMessages: [], loading: false });
  const [loadingWorkflows, setLoadingWorkflows] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [formError, setFormError] = useState<string>();
  const [listError, setListError] = useState<string>();
  const [integrations, setIntegrations] = useState<IntegrationSummary[]>([]);
  const [selectedIntegrationId, setSelectedIntegrationId] = useState<string>();
  const [integrationDetail, setIntegrationDetail] = useState<IntegrationDetail>();
  const [loadingIntegrations, setLoadingIntegrations] = useState(false);
  const [integrationError, setIntegrationError] = useState<string>();
  const [integrationSaved, setIntegrationSaved] = useState<string>();
  const [creatingIntegration, setCreatingIntegration] = useState(false);
  const [githubIntegrationForm, setGitHubIntegrationForm] = useState<GitHubIntegrationFormState>(initialGitHubIntegrationForm);
  const [repositoryForm, setRepositoryForm] = useState<RepositoryFormState>(initialRepositoryForm);

  const selectedWorkflow = useMemo(
    () => detail.workflow ?? workflows.find(workflow => workflow.workflowId === selectedWorkflowId),
    [detail.workflow, selectedWorkflowId, workflows]
  );

  const refreshWorkflows = useCallback(async () => {
    setLoadingWorkflows(true);
    setListError(undefined);
    try {
      const recent = await listWorkflows(25);
      setWorkflows(recent);
      if (!selectedWorkflowId && recent.length > 0) {
        setSelectedWorkflowId(recent[0].workflowId);
      }
    } catch (error) {
      setListError(error instanceof Error ? error.message : "Could not load workflows.");
    } finally {
      setLoadingWorkflows(false);
    }
  }, [selectedWorkflowId]);

  useEffect(() => {
    void refreshWorkflows();
  }, [refreshWorkflows]);

  useEffect(() => {
    let ignore = false;
    async function loadAiSettings() {
      setLoadingAiSettings(true);
      setAiSettingsError(undefined);
      try {
        const settings = await getAiSettings();
        if (ignore) {
          return;
        }

        setAiSettings(settings);
        setAiSettingsForm(toAiSettingsForm(settings));
        setForm(current => {
          if (modelTouched.current || current.model.trim()) {
            return current;
          }

          return { ...current, model: settings.model ?? "" };
        });
      } catch (error) {
        if (!ignore) {
          setAiSettingsError(error instanceof Error ? error.message : "Could not load AI settings.");
        }
      } finally {
        if (!ignore) {
          setLoadingAiSettings(false);
        }
      }
    }

    void loadAiSettings();
    return () => {
      ignore = true;
    };
  }, []);

  const refreshIntegrations = useCallback(async () => {
    setLoadingIntegrations(true);
    setIntegrationError(undefined);
    try {
      const items = await listIntegrations();
      setIntegrations(items);
      const nextSelectedId = selectedIntegrationId ?? items[0]?.id;
      setSelectedIntegrationId(nextSelectedId);
      if (nextSelectedId) {
        setIntegrationDetail(await getIntegration(nextSelectedId));
      } else {
        setIntegrationDetail(undefined);
      }
    } catch (error) {
      setIntegrationError(error instanceof Error ? error.message : "Could not load integrations.");
    } finally {
      setLoadingIntegrations(false);
    }
  }, [selectedIntegrationId]);

  useEffect(() => {
    if (activePage === "integrations") {
      void refreshIntegrations();
    }
  }, [activePage, refreshIntegrations]);

  useEffect(() => {
    if (!selectedWorkflowId) {
      setDetail({ runs: [], logs: [], events: [], signals: [], chatMessages: [], loading: false });
      return;
    }

    const workflowId = selectedWorkflowId;
    let ignore = false;
    async function loadDetail(showLoading = true) {
      if (showLoading) {
        setDetail(current => ({ ...current, loading: true, error: undefined }));
      }
      try {
        const [workflow, runs, logs, events, signals, chatMessages] = await Promise.all([
          getWorkflow(workflowId),
          listRuns(workflowId),
          listLogs(workflowId),
          listEvents(workflowId),
          listSignals(workflowId),
          listChatMessages(workflowId)
        ]);
        if (!ignore) {
          setDetail({ workflow, runs, logs, events, signals, chatMessages, loading: false });
        }
      } catch (error) {
        if (!ignore) {
          setDetail({
            workflow: workflows.find(workflow => workflow.workflowId === workflowId),
            runs: [],
            logs: [],
            events: [],
            signals: [],
            chatMessages: [],
            loading: false,
            error: error instanceof Error ? error.message : "Could not load workflow details."
          });
        }
      }
    }

    void loadDetail();
    const refreshInterval = window.setInterval(() => {
      void loadDetail(false);
    }, 3000);
    return () => {
      ignore = true;
      window.clearInterval(refreshInterval);
    };
  }, [selectedWorkflowId, workflows]);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setFormError(undefined);

    if (!form.issueUrl.trim() || !form.repositoryUrl.trim()) {
      setFormError("Issue URL and repository URL are required.");
      return;
    }

    setSubmitting(true);
    try {
      const workflow = await startWorkflow({
        issueUrl: form.issueUrl.trim(),
        repositoryUrl: form.repositoryUrl.trim(),
        baseBranch: form.baseBranch.trim() || "main",
        model: form.model.trim() || null
      });
      setSelectedWorkflowId(workflow.workflowId);
      setForm(current => ({ ...current, issueUrl: "", model: "" }));
      await refreshWorkflows();
    } catch (error) {
      setFormError(error instanceof Error ? error.message : "Could not start workflow.");
    } finally {
      setSubmitting(false);
    }
  }

  async function handleAiSettingsSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setAiSettingsError(undefined);
    setAiSettingsSaved(undefined);
    setSavingAiSettings(true);

    try {
      const settings = await updateAiSettings({
        provider: aiSettingsForm.provider.trim() || null,
        model: aiSettingsForm.model.trim() || null,
        authMethod: aiSettingsForm.authMethod,
        llmApiKeySecretName: aiSettingsForm.llmApiKeySecretName.trim() || null,
        endpointUrl: aiSettingsForm.endpointUrl.trim() || null
      });
      setAiSettings(settings);
      setAiSettingsForm(toAiSettingsForm(settings));
      setForm(current => {
        if (modelTouched.current || current.model.trim()) {
          return current;
        }

        return { ...current, model: settings.model ?? "" };
      });
      setAiSettingsSaved("Saved. New workflow executions will use these settings.");
    } catch (error) {
      setAiSettingsError(error instanceof Error ? error.message : "Could not save AI settings.");
    } finally {
      setSavingAiSettings(false);
    }
  }

  async function handleCreateIntegration(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setIntegrationError(undefined);
    setIntegrationSaved(undefined);
    setCreatingIntegration(true);
    try {
      const integration = await createGitHubIntegration({
        displayName: githubIntegrationForm.displayName.trim() || "GitHub",
        clientId: githubIntegrationForm.clientId.trim(),
        clientSecretReference: githubIntegrationForm.clientSecretReference.trim(),
        webhookSecret: null
      });
      setIntegrationDetail(integration);
      setSelectedIntegrationId(integration.id);
      setGitHubIntegrationForm(initialGitHubIntegrationForm);
      setIntegrationSaved("GitHub integration created.");
      await refreshIntegrations();
    } catch (error) {
      setIntegrationError(error instanceof Error ? error.message : "Could not create integration.");
    } finally {
      setCreatingIntegration(false);
    }
  }

  async function handleSelectIntegration(integrationId: string) {
    setSelectedIntegrationId(integrationId);
    setIntegrationError(undefined);
    setIntegrationSaved(undefined);
    try {
      setIntegrationDetail(await getIntegration(integrationId));
    } catch (error) {
      setIntegrationError(error instanceof Error ? error.message : "Could not load integration.");
    }
  }

  async function handleRotateWebhookSecret() {
    if (!selectedIntegrationId) {
      return;
    }

    setIntegrationError(undefined);
    setIntegrationSaved(undefined);
    try {
      setIntegrationDetail(await rotateWebhookSecret(selectedIntegrationId));
      setIntegrationSaved("Webhook secret rotated.");
    } catch (error) {
      setIntegrationError(error instanceof Error ? error.message : "Could not rotate webhook secret.");
    }
  }

  async function handleAddRepository(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!selectedIntegrationId) {
      return;
    }

    setIntegrationError(undefined);
    setIntegrationSaved(undefined);
    try {
      await addConnectedRepository(selectedIntegrationId, {
        repositoryUrl: repositoryForm.repositoryUrl.trim(),
        defaultBranch: repositoryForm.defaultBranch.trim() || "main",
        installationId: repositoryForm.installationId.trim() ? Number(repositoryForm.installationId) : null,
        installationAccount: repositoryForm.installationAccount.trim() || null
      });
      setRepositoryForm(initialRepositoryForm);
      setIntegrationDetail(await getIntegration(selectedIntegrationId));
      setIntegrationSaved("Repository connected.");
    } catch (error) {
      setIntegrationError(error instanceof Error ? error.message : "Could not connect repository.");
    }
  }

  async function handleIdentityToggle(enabled: boolean) {
    if (!selectedIntegrationId) {
      return;
    }

    setIntegrationError(undefined);
    setIntegrationSaved(undefined);
    try {
      setIntegrationDetail(await setIdentityProviderEnabled(selectedIntegrationId, enabled));
      setIntegrationSaved(enabled ? "Identity provider enabled." : "Identity provider disabled.");
      await refreshIntegrations();
    } catch (error) {
      setIntegrationError(error instanceof Error ? error.message : "Could not update identity provider.");
    }
  }

  return (
    <main className="app-shell">
      <header className="topbar">
        <div>
          <p className="eyebrow">Formicae</p>
          <h1>{activePage === "workflows" ? "Workflow Management" : activePage === "integrations" ? "Integrations" : "Settings"}</h1>
        </div>
        <div className="topbar-actions">
          <nav className="app-menu" aria-label="Primary navigation">
            <button
              type="button"
              className={`menu-button${activePage === "workflows" ? " active" : ""}`}
              onClick={() => setActivePage("workflows")}
            >
              Workflows
            </button>
            <button
              type="button"
              className={`menu-button${activePage === "integrations" ? " active" : ""}`}
              onClick={() => setActivePage("integrations")}
            >
              Integrations
            </button>
            <button
              type="button"
              className={`menu-button${activePage === "settings" ? " active" : ""}`}
              onClick={() => setActivePage("settings")}
            >
              Settings
            </button>
          </nav>
          {activePage === "workflows" ? (
            <button type="button" className="secondary-button" onClick={() => void refreshWorkflows()} disabled={loadingWorkflows}>
              {loadingWorkflows ? "Refreshing" : "Refresh"}
            </button>
          ) : activePage === "integrations" ? (
            <button type="button" className="secondary-button" onClick={() => void refreshIntegrations()} disabled={loadingIntegrations}>
              {loadingIntegrations ? "Refreshing" : "Refresh"}
            </button>
          ) : null}
        </div>
      </header>
      {activePage === "workflows" ? (
        <>
          <section className="workspace-grid">
        <div className="left-stack">
          <form className="panel trigger-panel" onSubmit={handleSubmit}>
            <div className="panel-heading">
              <h2>Manual Trigger</h2>
            </div>
            <label>
              <span>Issue URL</span>
              <input
                value={form.issueUrl}
                onChange={event => setForm(current => ({ ...current, issueUrl: event.target.value }))}
                placeholder="https://github.com/org/repo/issues/1"
                type="url"
              />
            </label>
            <label>
              <span>Repository URL</span>
              <input
                value={form.repositoryUrl}
                onChange={event => setForm(current => ({ ...current, repositoryUrl: event.target.value }))}
                placeholder="https://github.com/org/repo"
                type="url"
              />
            </label>
            <div className="form-row">
              <label>
                <span>Base Branch</span>
                <input
                  value={form.baseBranch}
                  onChange={event => setForm(current => ({ ...current, baseBranch: event.target.value }))}
                />
              </label>
              <label>
                <span>Model</span>
                <input
                  value={form.model}
                  onChange={event => {
                    modelTouched.current = true;
                    setForm(current => ({ ...current, model: event.target.value }));
                  }}
                  placeholder="optional"
                />
              </label>
            </div>
            {formError ? <p className="error-text">{formError}</p> : null}
            <button type="submit" className="primary-button" disabled={submitting}>
              {submitting ? "Starting" : "Start Workflow"}
            </button>
          </form>
        </div>

        <section className="panel recent-panel">
          <div className="panel-heading">
            <h2>Recent Runs</h2>
            {listError ? <span className="error-text">{listError}</span> : null}
          </div>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Created</th>
                  <th>Status</th>
                  <th>Step</th>
                  <th>Issue</th>
                  <th>Pull Request</th>
                  <th>Failure</th>
                </tr>
              </thead>
              <tbody>
                {workflows.map(workflow => (
                  <tr
                    key={workflow.workflowId}
                    className={workflow.workflowId === selectedWorkflowId ? "selected-row" : undefined}
                    onClick={() => setSelectedWorkflowId(workflow.workflowId)}
                  >
                    <td>{formatDate(workflow.createdAt)}</td>
                    <td><StatusBadge value={formatEnum(workflow.status, workflowStatuses)} /></td>
                    <td>{formatEnum(workflow.currentStep, workflowSteps)}</td>
                    <td><ExternalLink href={workflow.issueUrl}>{shortUrl(workflow.issueUrl)}</ExternalLink></td>
                    <td>{workflow.pullRequestUrl ? <ExternalLink href={workflow.pullRequestUrl}>Open</ExternalLink> : <span className="muted">None</span>}</td>
                    <td>{workflow.failureReason ? <span className="failure-cell">{workflow.failureReason}</span> : <span className="muted">None</span>}</td>
                  </tr>
                ))}
                {workflows.length === 0 ? (
                  <tr>
                    <td colSpan={6} className="empty-cell">{loadingWorkflows ? "Loading workflows" : "No workflows yet"}</td>
                  </tr>
                ) : null}
              </tbody>
            </table>
          </div>
        </section>
      </section>

      <section className="panel detail-panel">
        <div className="panel-heading">
          <h2>Workflow Detail</h2>
          {detail.loading ? <span className="muted">Loading</span> : null}
        </div>
        {selectedWorkflow ? (
          <div className="detail-grid">
            <div className="summary-list">
              <SummaryItem label="Workflow ID" value={selectedWorkflow.workflowId} mono />
              <SummaryItem label="Status" value={formatEnum(selectedWorkflow.status, workflowStatuses)} />
              <SummaryItem label="Current Step" value={formatEnum(selectedWorkflow.currentStep, workflowSteps)} />
              <SummaryItem label="Issue" value={<ExternalLink href={selectedWorkflow.issueUrl}>{selectedWorkflow.issueUrl}</ExternalLink>} />
              <SummaryItem label="Pull Request" value={selectedWorkflow.pullRequestUrl ? <ExternalLink href={selectedWorkflow.pullRequestUrl}>{selectedWorkflow.pullRequestUrl}</ExternalLink> : "None"} />
              <SummaryItem label="Failure" value={selectedWorkflow.failureReason ?? "None"} />
            </div>

            <div className="detail-stack">
              {detail.error ? <p className="error-text">{detail.error}</p> : null}
              {detail.signals.length > 0 ? (
                <section>
                  <h3>Signals</h3>
                  <div className="signal-list">
                    {detail.signals.map(signal => (
                      <div className={`signal-row signal-${signal.severity.toLowerCase()}`} key={`${signal.taskRunId ?? "workflow"}-${signal.reason}`}>
                        <strong>{signal.severity}</strong>
                        <span>{signal.reason}</span>
                        <time>{formatDate(signal.observedAt)}</time>
                      </div>
                    ))}
                  </div>
                </section>
              ) : null}

              <section>
                <h3>Timeline</h3>
                <div className="timeline-list">
                  {detail.events.map(event => (
                    <article className="timeline-item" key={event.id}>
                      <div className="timeline-meta">
                        <time>{formatDate(event.createdAt)}</time>
                        <StatusBadge value={event.type} />
                        <span>{event.level}</span>
                      </div>
                      <p>{event.message}</p>
                      {event.detailsJson ? <Expandable title="Event Details" content={formatJson(event.detailsJson)} pre /> : null}
                    </article>
                  ))}
                  {detail.events.length === 0 ? <p className="muted">No events recorded.</p> : null}
                </div>
              </section>

              <section>
                <h3>Task Runs</h3>
                <div className="run-list">
                  {detail.runs.map(run => (
                    <article className="run-card" key={run.id}>
                      <div className="run-meta">
                        <strong>{formatEnum(run.kind, taskRunKinds)}</strong>
                        <StatusBadge value={formatEnum(run.status, taskRunStatuses)} />
                        <span>{formatDate(run.updatedAt)}</span>
                        <span>{formatDuration(run.startedAt, run.completedAt)}</span>
                      </div>
                      {run.failureReason ? <p className="error-text">{run.failureReason}</p> : null}
                      {run.agentMessages.length > 0 ? (
                        <div className="agent-message-list">
                          {run.agentMessages.map(message => (
                            <Expandable
                              key={message.sequence}
                              title={`Agent Message ${message.sequence + 1}${message.role ? `: ${message.role}` : ""}`}
                              content={message.content}
                            />
                          ))}
                        </div>
                      ) : null}
                      {run.output ? <Expandable title="Raw Output" content={run.output} pre /> : <p className="muted">No output recorded.</p>}
                    </article>
                  ))}
                  {detail.runs.length === 0 ? <p className="muted">No task runs recorded.</p> : null}
                </div>
              </section>

              <section>
                <h3>Chat Messages</h3>
                <div className="chat-list">
                  {detail.chatMessages.map(message => (
                    <article className="chat-row" key={message.id}>
                      <div className="chat-meta">
                        <strong>{message.author}</strong>
                        <time>{formatDate(message.updatedAt)}</time>
                        <ExternalLink href={message.url}>Open</ExternalLink>
                      </div>
                      <Expandable title="Message" content={message.body} />
                    </article>
                  ))}
                  {detail.chatMessages.length === 0 ? <p className="muted">No chat messages recorded.</p> : null}
                </div>
              </section>

              <section>
                <h3>Logs</h3>
                <div className="log-list">
                  {detail.logs.map(log => (
                    <div className="log-row" key={log.id}>
                      <time>{formatDate(log.createdAt)}</time>
                      <span>{log.level}</span>
                      <Expandable title="Log Message" content={log.message} />
                    </div>
                  ))}
                  {detail.logs.length === 0 ? <p className="muted">No logs recorded.</p> : null}
                </div>
              </section>
            </div>
          </div>
        ) : (
          <p className="muted">Select a workflow to inspect runs and logs.</p>
        )}
      </section>
        </>
      ) : activePage === "integrations" ? (
        <IntegrationsPage
          integrations={integrations}
          integrationDetail={integrationDetail}
          selectedIntegrationId={selectedIntegrationId}
          integrationError={integrationError}
          integrationSaved={integrationSaved}
          githubIntegrationForm={githubIntegrationForm}
          repositoryForm={repositoryForm}
          creatingIntegration={creatingIntegration}
          setGitHubIntegrationForm={setGitHubIntegrationForm}
          setRepositoryForm={setRepositoryForm}
          onCreateIntegration={handleCreateIntegration}
          onSelectIntegration={handleSelectIntegration}
          onRotateWebhookSecret={handleRotateWebhookSecret}
          onAddRepository={handleAddRepository}
          onIdentityToggle={handleIdentityToggle}
        />
      ) : (
        <SettingsPage
          aiSettings={aiSettings}
          aiSettingsForm={aiSettingsForm}
          loadingAiSettings={loadingAiSettings}
          savingAiSettings={savingAiSettings}
          aiSettingsError={aiSettingsError}
          aiSettingsSaved={aiSettingsSaved}
          setAiSettingsForm={setAiSettingsForm}
          onSubmit={handleAiSettingsSubmit}
        />
      )}
    </main>
  );
}

function IntegrationsPage({
  integrations,
  integrationDetail,
  selectedIntegrationId,
  integrationError,
  integrationSaved,
  githubIntegrationForm,
  repositoryForm,
  creatingIntegration,
  setGitHubIntegrationForm,
  setRepositoryForm,
  onCreateIntegration,
  onSelectIntegration,
  onRotateWebhookSecret,
  onAddRepository,
  onIdentityToggle
}: {
  integrations: IntegrationSummary[];
  integrationDetail?: IntegrationDetail;
  selectedIntegrationId?: string;
  integrationError?: string;
  integrationSaved?: string;
  githubIntegrationForm: GitHubIntegrationFormState;
  repositoryForm: RepositoryFormState;
  creatingIntegration: boolean;
  setGitHubIntegrationForm: Dispatch<SetStateAction<GitHubIntegrationFormState>>;
  setRepositoryForm: Dispatch<SetStateAction<RepositoryFormState>>;
  onCreateIntegration: (event: FormEvent<HTMLFormElement>) => void;
  onSelectIntegration: (integrationId: string) => void;
  onRotateWebhookSecret: () => void;
  onAddRepository: (event: FormEvent<HTMLFormElement>) => void;
  onIdentityToggle: (enabled: boolean) => void;
}) {
  return (
    <section className="integrations-page">
      <section className="workspace-grid">
        <div className="left-stack">
          <form className="panel" onSubmit={onCreateIntegration}>
            <div className="panel-heading">
              <h2>GitHub App</h2>
            </div>
            <label>
              <span>Display Name</span>
              <input
                value={githubIntegrationForm.displayName}
                onChange={event => setGitHubIntegrationForm(current => ({ ...current, displayName: event.target.value }))}
              />
            </label>
            <label>
              <span>Client ID</span>
              <input
                value={githubIntegrationForm.clientId}
                onChange={event => setGitHubIntegrationForm(current => ({ ...current, clientId: event.target.value }))}
              />
            </label>
            <label>
              <span>Client Secret Reference</span>
              <input
                value={githubIntegrationForm.clientSecretReference}
                onChange={event => setGitHubIntegrationForm(current => ({ ...current, clientSecretReference: event.target.value }))}
                placeholder="Kubernetes secret key or secure reference"
              />
            </label>
            <button type="submit" className="primary-button" disabled={creatingIntegration}>
              {creatingIntegration ? "Creating" : "Create Integration"}
            </button>
          </form>

          <section className="panel">
            <div className="panel-heading">
              <h2>Configured</h2>
            </div>
            <div className="integration-list">
              {integrations.map(integration => (
                <button
                  type="button"
                  className={`integration-row${integration.id === selectedIntegrationId ? " selected" : ""}`}
                  key={integration.id}
                  onClick={() => onSelectIntegration(integration.id)}
                >
                  <strong>{integration.displayName}</strong>
                  <span>{integration.providerType}</span>
                </button>
              ))}
              {integrations.length === 0 ? <p className="muted">No integrations configured.</p> : null}
            </div>
          </section>
        </div>

        <section className="panel">
          <div className="panel-heading">
            <h2>Integration Detail</h2>
            {integrationDetail ? <StatusBadge value={integrationDetail.identityProviderEnabled ? "IdentityOn" : "IdentityOff"} /> : null}
          </div>
          {integrationError ? <p className="error-text">{integrationError}</p> : null}
          {integrationSaved ? <p className="success-text">{integrationSaved}</p> : null}

          {integrationDetail ? (
            <div className="detail-stack">
              <div className="summary-list compact">
                <SummaryItem label="Client ID" value={integrationDetail.gitHubAppClientId} mono />
                <SummaryItem label="Webhook URL" value={integrationDetail.webhookUrl} mono />
                <SummaryItem label="Webhook Secret" value={integrationDetail.webhookSecret} mono />
                <SummaryItem label="Callback URL" value={integrationDetail.setupInstructions.callbackUrl} mono />
              </div>

              <div className="button-row">
                <button type="button" className="secondary-button" onClick={onRotateWebhookSecret}>Rotate Webhook Secret</button>
                <label className="toggle-label">
                  <input
                    type="checkbox"
                    checked={integrationDetail.identityProviderEnabled}
                    onChange={event => onIdentityToggle(event.target.checked)}
                  />
                  <span>Use as identity provider</span>
                </label>
              </div>
              {integrationDetail.requiresRestart ? (
                <p className="warning-text">Restart required before GitHub login uses this integration.</p>
              ) : null}

              <section>
                <h3>GitHub App Setup</h3>
                <div className="checklist-grid">
                  <div>
                    <h4>Repository permissions</h4>
                    <ul>
                      {integrationDetail.setupInstructions.requiredRepositoryPermissions.map(permission => <li key={permission}>{permission}</li>)}
                    </ul>
                  </div>
                  <div>
                    <h4>Webhook events</h4>
                    <ul>
                      {integrationDetail.setupInstructions.requiredWebhookEvents.map(eventName => <li key={eventName}>{eventName}</li>)}
                    </ul>
                  </div>
                </div>
              </section>

              <section>
                <h3>Connected Repositories</h3>
                <form className="repository-form" onSubmit={onAddRepository}>
                  <input
                    value={repositoryForm.repositoryUrl}
                    onChange={event => setRepositoryForm(current => ({ ...current, repositoryUrl: event.target.value }))}
                    placeholder="https://github.com/org/repo"
                    type="url"
                  />
                  <input
                    value={repositoryForm.defaultBranch}
                    onChange={event => setRepositoryForm(current => ({ ...current, defaultBranch: event.target.value }))}
                    placeholder="main"
                  />
                  <input
                    value={repositoryForm.installationId}
                    onChange={event => setRepositoryForm(current => ({ ...current, installationId: event.target.value }))}
                    placeholder="installation id"
                    type="number"
                  />
                  <input
                    value={repositoryForm.installationAccount}
                    onChange={event => setRepositoryForm(current => ({ ...current, installationAccount: event.target.value }))}
                    placeholder="installation account"
                  />
                  <button type="submit" className="secondary-button">Add</button>
                </form>
                <div className="repository-list">
                  {integrationDetail.repositories.map(repository => <RepositoryRow repository={repository} key={repository.id} />)}
                  {integrationDetail.repositories.length === 0 ? <p className="muted">No repositories connected.</p> : null}
                </div>
              </section>
            </div>
          ) : (
            <p className="muted">Create or select an integration.</p>
          )}
        </section>
      </section>
    </section>
  );
}

function RepositoryRow({ repository }: { repository: ConnectedRepository }) {
  return (
    <div className="repository-row">
      <div>
        <strong>{repository.owner}/{repository.name}</strong>
        <span>{repository.defaultBranch}</span>
      </div>
      <ExternalLink href={repository.repositoryUrl}>Open</ExternalLink>
    </div>
  );
}

function SettingsPage({
  aiSettings,
  aiSettingsForm,
  loadingAiSettings,
  savingAiSettings,
  aiSettingsError,
  aiSettingsSaved,
  setAiSettingsForm,
  onSubmit
}: {
  aiSettings?: AiSettings;
  aiSettingsForm: AiSettingsFormState;
  loadingAiSettings: boolean;
  savingAiSettings: boolean;
  aiSettingsError?: string;
  aiSettingsSaved?: string;
  setAiSettingsForm: Dispatch<SetStateAction<AiSettingsFormState>>;
  onSubmit: (event: FormEvent<HTMLFormElement>) => void;
}) {
  return (
    <section className="settings-page">
      <section className="panel settings-panel">
        <div className="panel-heading">
          <h2>AI Settings</h2>
          {loadingAiSettings ? <span className="muted">Loading</span> : null}
        </div>
        <form onSubmit={onSubmit}>
          <div className="settings-section">
            <h3>Basic</h3>
            <div className="form-row">
              <label>
                <span>Provider</span>
                <input
                  value={aiSettingsForm.provider}
                  onChange={event => setAiSettingsForm(current => ({ ...current, provider: event.target.value }))}
                  placeholder="OpenAI, Anthropic, custom"
                />
              </label>
              <label>
                <span>Model</span>
                <input
                  value={aiSettingsForm.model}
                  onChange={event => setAiSettingsForm(current => ({ ...current, model: event.target.value }))}
                  placeholder="optional default"
                />
              </label>
            </div>
            <label>
              <span>Auth Method</span>
              <select
                value={aiSettingsForm.authMethod}
                onChange={event => setAiSettingsForm(current => ({ ...current, authMethod: event.target.value }))}
              >
                <option value="ApiKey">API key</option>
                <option value="CodexSubscription">Codex subscription</option>
              </select>
            </label>
            <label>
              <span>API Key Secret Name</span>
              <input
                value={aiSettingsForm.llmApiKeySecretName}
                onChange={event => setAiSettingsForm(current => ({ ...current, llmApiKeySecretName: event.target.value }))}
                placeholder="Kubernetes secret name"
              />
            </label>
            <div className="secret-status">
              <span>API key</span>
              <StatusBadge value={aiSettings?.hasApiKeySecret ? "Configured" : "NotConfigured"} />
            </div>
          </div>
          <div className="settings-section">
            <h3>Advanced</h3>
            <label>
              <span>Endpoint / Base URL</span>
              <input
                value={aiSettingsForm.endpointUrl}
                onChange={event => setAiSettingsForm(current => ({ ...current, endpointUrl: event.target.value }))}
                placeholder="https://api.example.com/v1"
                type="url"
              />
            </label>
          </div>
          {aiSettingsError ? <p className="error-text">{aiSettingsError}</p> : null}
          {aiSettingsSaved ? <p className="success-text">{aiSettingsSaved}</p> : null}
          <button type="submit" className="primary-button" disabled={savingAiSettings}>
            {savingAiSettings ? "Saving" : "Save AI Settings"}
          </button>
        </form>
      </section>
    </section>
  );
}
function toAiSettingsForm(settings: AiSettings): AiSettingsFormState {
  return {
    provider: settings.provider ?? "",
    model: settings.model ?? "",
    authMethod: settings.authMethod,
    llmApiKeySecretName: settings.llmApiKeySecretName ?? "",
    endpointUrl: settings.endpointUrl ?? ""
  };
}

function SummaryItem({ label, value, mono }: { label: string; value: ReactNode; mono?: boolean }) {
  return (
    <div className="summary-item">
      <dt>{label}</dt>
      <dd className={mono ? "mono" : undefined}>{value}</dd>
    </div>
  );
}

function StatusBadge({ value }: { value: string }) {
  return <span className={`status-badge status-${value.toLowerCase()}`}>{value}</span>;
}

function ExternalLink({ href, children }: { href: string; children: ReactNode }) {
  return <a href={href} target="_blank" rel="noreferrer">{children}</a>;
}

function Expandable({ title, content, pre }: { title: string; content: string; pre?: boolean }) {
  const [expanded, setExpanded] = useState(false);
  return (
    <div className="expandable">
      <button type="button" className="expand-button" onClick={() => setExpanded(current => !current)}>
        {expanded ? "Collapse" : "Expand"} {title}
      </button>
      {pre ? (
        <pre className={expanded ? "expanded-content" : "collapsed-content"}>{content}</pre>
      ) : (
        <p className={expanded ? "expanded-content prose-content" : "collapsed-content prose-content"}>{content}</p>
      )}
    </div>
  );
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "short",
    timeStyle: "medium"
  }).format(new Date(value));
}

function formatEnum(value: string | number, labels: string[]) {
  if (typeof value === "number") {
    return labels[value] ?? String(value);
  }

  return value;
}

function formatDuration(startedAt?: string | null, completedAt?: string | null) {
  if (!startedAt) {
    return "Not started";
  }

  const start = new Date(startedAt).getTime();
  const end = completedAt ? new Date(completedAt).getTime() : Date.now();
  if (!Number.isFinite(start) || !Number.isFinite(end) || end < start) {
    return "Duration unavailable";
  }

  const seconds = Math.round((end - start) / 1000);
  if (seconds < 60) {
    return `${seconds}s`;
  }

  const minutes = Math.floor(seconds / 60);
  const remainingSeconds = seconds % 60;
  if (minutes < 60) {
    return `${minutes}m ${remainingSeconds}s`;
  }

  const hours = Math.floor(minutes / 60);
  return `${hours}h ${minutes % 60}m`;
}

function formatJson(value: string) {
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}

function shortUrl(value: string) {
  try {
    const url = new URL(value);
    return `${url.hostname}${url.pathname}`;
  } catch {
    return value;
  }
}
