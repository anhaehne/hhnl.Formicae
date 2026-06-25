import { FormEvent, useCallback, useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import {
  getWorkflow,
  listLogs,
  listRuns,
  listWorkflows,
  startWorkflow,
  TaskRun,
  WorkflowLog,
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

type DetailState = {
  workflow?: WorkflowSummary;
  runs: TaskRun[];
  logs: WorkflowLog[];
  loading: boolean;
  error?: string;
};

const initialForm: FormState = {
  issueUrl: "",
  repositoryUrl: "",
  baseBranch: "main",
  model: ""
};

export default function App() {
  const [form, setForm] = useState<FormState>(initialForm);
  const [workflows, setWorkflows] = useState<WorkflowSummary[]>([]);
  const [selectedWorkflowId, setSelectedWorkflowId] = useState<string>();
  const [detail, setDetail] = useState<DetailState>({ runs: [], logs: [], loading: false });
  const [loadingWorkflows, setLoadingWorkflows] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [formError, setFormError] = useState<string>();
  const [listError, setListError] = useState<string>();

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
    if (!selectedWorkflowId) {
      setDetail({ runs: [], logs: [], loading: false });
      return;
    }

    const workflowId = selectedWorkflowId;
    let ignore = false;
    async function loadDetail() {
      setDetail(current => ({ ...current, loading: true, error: undefined }));
      try {
        const [workflow, runs, logs] = await Promise.all([
          getWorkflow(workflowId),
          listRuns(workflowId),
          listLogs(workflowId)
        ]);
        if (!ignore) {
          setDetail({ workflow, runs, logs, loading: false });
        }
      } catch (error) {
        if (!ignore) {
          setDetail({
            workflow: workflows.find(workflow => workflow.workflowId === workflowId),
            runs: [],
            logs: [],
            loading: false,
            error: error instanceof Error ? error.message : "Could not load workflow details."
          });
        }
      }
    }

    void loadDetail();
    return () => {
      ignore = true;
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

  return (
    <main className="app-shell">
      <header className="topbar">
        <div>
          <p className="eyebrow">Formicae</p>
          <h1>Workflow Management</h1>
        </div>
        <button type="button" className="secondary-button" onClick={() => void refreshWorkflows()} disabled={loadingWorkflows}>
          {loadingWorkflows ? "Refreshing" : "Refresh"}
        </button>
      </header>

      <section className="workspace-grid">
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
                onChange={event => setForm(current => ({ ...current, model: event.target.value }))}
                placeholder="optional"
              />
            </label>
          </div>
          {formError ? <p className="error-text">{formError}</p> : null}
          <button type="submit" className="primary-button" disabled={submitting}>
            {submitting ? "Starting" : "Start Workflow"}
          </button>
        </form>

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
              <section>
                <h3>Task Runs</h3>
                <div className="run-list">
                  {detail.runs.map(run => (
                    <article className="run-card" key={run.id}>
                      <div className="run-meta">
                        <strong>{formatEnum(run.kind, taskRunKinds)}</strong>
                        <StatusBadge value={formatEnum(run.status, taskRunStatuses)} />
                        <span>{formatDate(run.updatedAt)}</span>
                      </div>
                      {run.failureReason ? <p className="error-text">{run.failureReason}</p> : null}
                      {run.output ? <pre>{run.output}</pre> : <p className="muted">No output recorded.</p>}
                    </article>
                  ))}
                  {detail.runs.length === 0 ? <p className="muted">No task runs recorded.</p> : null}
                </div>
              </section>

              <section>
                <h3>Logs</h3>
                <div className="log-list">
                  {detail.logs.map(log => (
                    <div className="log-row" key={log.id}>
                      <time>{formatDate(log.createdAt)}</time>
                      <span>{log.level}</span>
                      <p>{log.message}</p>
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
    </main>
  );
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

function shortUrl(value: string) {
  try {
    const url = new URL(value);
    return `${url.hostname}${url.pathname}`;
  } catch {
    return value;
  }
}
