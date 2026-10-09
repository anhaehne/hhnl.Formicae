import { useEffect, useRef, useState, type FormEvent } from "react";
import { startWorkflow, type WorkflowDefinitionResponse } from "./api";
import { getEnabledDefinitionVersions } from "./workflowGraph";

export default function ManualWorkflowStart({ definition, selectedVersionId, defaultModel, canTrigger, dirty, onStarted }: {
  definition: WorkflowDefinitionResponse; selectedVersionId?: string; defaultModel: string;
  canTrigger: boolean; dirty: boolean; onStarted: (id: string) => void;
}) {
  const versions = getEnabledDefinitionVersions([definition]).map(item => item.version);
  const [versionId, setVersionId] = useState("");
  const [issueUrl, setIssueUrl] = useState(""), [repositoryUrl, setRepositoryUrl] = useState(""), [baseBranch, setBaseBranch] = useState("main");
  const [model, setModel] = useState(defaultModel), modelTouched = useRef(false);
  const [busy, setBusy] = useState(false), [error, setError] = useState<string>();
  useEffect(() => { if (!modelTouched.current) setModel(defaultModel); }, [defaultModel]);
  useEffect(() => {
    setVersionId(versions.find(version => version.id === selectedVersionId)?.id ?? versions[0]?.id ?? "");
    setError(undefined);
  }, [selectedVersionId, definition]);
  const selectedVersion = versions.find(version => version.id === versionId);
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy || !canTrigger || dirty) return;
    setError(undefined);
    if (!issueUrl.trim() || !repositoryUrl.trim()) { setError("Issue URL and repository URL are required."); return; }
    if (!selectedVersion) { setError("Select an enabled version with a manual Start event."); return; }
    setBusy(true);
    try {
      const workflow = await startWorkflow({ issueUrl: issueUrl.trim(), repositoryUrl: repositoryUrl.trim(), baseBranch: baseBranch.trim() || "main", model: model.trim() || null, workflowDefinitionId: definition.id, workflowDefinitionVersionId: selectedVersion.id });
      onStarted(workflow.workflowId);
    } catch (reason) { setError(reason instanceof Error ? reason.message : "Could not start workflow."); }
    finally { setBusy(false); }
  }
  return <form className="definition-manual-start" aria-label="Manual Start" onSubmit={submit}>
    <h3>Manual Start</h3><p className="muted">Start a saved version of {definition.name}.</p>
    <label>Version<select aria-label="Version" value={versionId} onChange={event => setVersionId(event.target.value)} disabled={busy || !canTrigger || !versions.length}>{versions.map(version => <option key={version.id} value={version.id}>v{version.version}{version.isDefault ? " (default)" : ""}</option>)}</select></label>
    <label>Issue URL<input type="url" value={issueUrl} onChange={event => setIssueUrl(event.target.value)} placeholder="https://github.com/org/repo/issues/1" required disabled={busy || !canTrigger} /></label>
    <label>Repository URL<input type="url" value={repositoryUrl} onChange={event => setRepositoryUrl(event.target.value)} placeholder="https://github.com/org/repo" required disabled={busy || !canTrigger} /></label>
    <div className="form-row"><label>Base Branch<input value={baseBranch} onChange={event => setBaseBranch(event.target.value)} disabled={busy || !canTrigger} /></label><label>Model<input value={model} onChange={event => { modelTouched.current = true; setModel(event.target.value); }} placeholder="optional" disabled={busy || !canTrigger} /></label></div>
    {!versions.length && <p role="status">This definition has no enabled version with a manual Start event.</p>}
    {!canTrigger && <p role="status">Workflow command permission is required to start a run.</p>}
    {dirty && <p role="status">Save or discard your definition changes before starting a workflow.</p>}
    {error && <p className="error-text" role="alert">{error}</p>}
    <button type="submit" className="primary-button" disabled={busy || !canTrigger || dirty || !selectedVersion}>{busy ? "Starting" : "Start Workflow"}</button>
  </form>;
}
