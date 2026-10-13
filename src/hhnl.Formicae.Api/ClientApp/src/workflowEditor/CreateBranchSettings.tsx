import { useEffect, useState } from "react";
import { getIntegration, listIntegrations, listRepositoryBranches, type IntegrationDetail, type WorkflowCreateBranchSettings } from "../api";

export function CreateBranchSettings({ value, disabled, onChange, boundInputs = [] }: { boundInputs?: string[]; value?: WorkflowCreateBranchSettings | null; disabled: boolean; onChange: (value: WorkflowCreateBranchSettings) => void }) {
  const settings = value ?? { repositoryId: "", sourceBranch: "", branchName: "" };
  const [integrations, setIntegrations] = useState<IntegrationDetail[]>([]);
  const [repositoriesLoading, setRepositoriesLoading] = useState(true);
  const [repositoryError, setRepositoryError] = useState("");
  const [branches, setBranches] = useState<string[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [reload, setReload] = useState(0);
  const repository = integrations.flatMap(integration => integration.repositories.map(item => ({ ...item, integrationId: integration.id }))).find(item => item.id === settings.repositoryId);
  useEffect(() => {
    let canceled = false;
    setRepositoriesLoading(true); setRepositoryError("");
    listIntegrations().then(items => Promise.all(items.filter(item => item.providerType === "GitHub").map(item => getIntegration(item.id))))
      .then(items => { if (!canceled) setIntegrations(items); })
      .catch(() => { if (!canceled) setRepositoryError("Could not load connected GitHub repositories."); })
      .finally(() => { if (!canceled) setRepositoriesLoading(false); });
    return () => { canceled = true; };
  }, [reload]);
  useEffect(() => {
    let canceled = false;
    setBranches([]); setError(""); setLoading(Boolean(repository));
    if (repository) listRepositoryBranches(repository.integrationId, repository.id)
      .then(items => { if (!canceled) setBranches(items); })
      .catch(() => { if (!canceled) setError("Could not load source branches. Check repository access and retry."); })
      .finally(() => { if (!canceled) setLoading(false); });
    return () => { canceled = true; };
  }, [repository?.integrationId, repository?.id, reload]);
  return <section className="editor-property-section"><h4>Create branch</h4>
    <label><span>Connected GitHub repository</span><select aria-label="Connected GitHub repository" disabled={disabled || boundInputs.includes("repository") || repositoriesLoading} value={settings.repositoryId} onChange={event => onChange({ ...settings, repositoryId: event.target.value, sourceBranch: "" })}>
      <option value="">Select repository</option>
      {settings.repositoryId && !repository && <option value={settings.repositoryId} disabled>Selected repository unavailable</option>}
      {integrations.map(integration => <optgroup key={integration.id} label={integration.displayName}>{integration.repositories.map(item => <option key={item.id} value={item.id}>{item.owner}/{item.name}</option>)}</optgroup>)}
    </select></label>
    {repositoriesLoading ? <p className="muted">Loading repositories…</p> : repositoryError ? <p role="alert">{repositoryError}</p> : !integrations.some(item => item.repositories.length) && <p className="muted">No connected GitHub repositories.</p>}
    <label><span>Source branch</span><select aria-label="Source branch" disabled={disabled || boundInputs.includes("sourceBranch") || !repository || loading || Boolean(error)} value={settings.sourceBranch} onChange={event => onChange({ ...settings, sourceBranch: event.target.value })}>
      <option value="">Select source branch</option>
      {settings.sourceBranch && !branches.includes(settings.sourceBranch) && <option value={settings.sourceBranch} disabled>{settings.sourceBranch}{loading ? "" : " (unavailable)"}</option>}
      {branches.map(branch => <option key={branch} value={branch}>{branch}</option>)}
    </select></label>
    {loading && <p className="muted">Loading source branches…</p>}
    {error && <p role="alert">{error}</p>}
    {!loading && !error && repository && branches.length === 0 && <p className="muted">This repository has no source branches.</p>}
    {(error || repositoryError) && <button type="button" disabled={disabled} onClick={() => setReload(count => count + 1)}>Retry loading branches</button>}
    <label><span>New branch name</span><input aria-label="New branch name" disabled={disabled || boundInputs.includes("targetBranch")} value={settings.branchName} onChange={event => onChange({ ...settings, branchName: event.target.value })} placeholder="feature/my-change" /></label>
    {["repository", "sourceBranch", "targetBranch"].map(name => <label key={name}><span>{name} input</span><input aria-label={`${name} input value`} disabled={disabled || boundInputs.includes(name)} value={String(settings.inputs?.[name] ?? "")} placeholder={name === "repository" ? "https://github.com/owner/repository" : name === "sourceBranch" ? "main" : "feature/my-change"} onChange={event => { const inputs = { ...settings.inputs }; if (event.target.value) inputs[name] = event.target.value; else delete inputs[name]; onChange({ ...settings, inputs }); }} /></label>)}
    <p className="muted">Input values override the selections above. Leave blank to use the selections, or connect a port.</p>
    {boundInputs.length > 0 && <p className="muted">Connected inputs: {boundInputs.join(", ")}. Values resolve when this task runs.</p>}
    <p className="muted">Connect repository, sourceBranch and targetBranch ports to use upstream values.</p>
    <p className="muted">Creates the new branch at the source branch’s commit when this task runs. Existing branches are never overwritten.</p>
  </section>;
}
