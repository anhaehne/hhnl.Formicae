import { useEffect, useState } from "react";
import { searchWorkflows, type WorkflowDefinitionResponse, type WorkflowFilters, type WorkflowSearchPage } from "./api";
import { duration, enumName, StateBadge, workflowStates } from "./workflowExecution/evidence";
type SavedView = { name: string; filters: WorkflowFilters };
const storageKey = "formicae.workflow-views.v1";
function savedViews(): SavedView[] { try { const value = JSON.parse(localStorage.getItem(storageKey) ?? "[]"); return Array.isArray(value) ? value.filter(item => typeof item?.name === "string" && item.filters && typeof item.filters === "object").slice(0, 20) : []; } catch { return []; } }
export default function WorkflowHistory({ selectedId, onSelect, definitions, refreshToken }: { selectedId?: string; onSelect: (id: string) => void; definitions: WorkflowDefinitionResponse[]; refreshToken: unknown }) {
 const [filters, setFilters] = useState<WorkflowFilters>({}), [offset, setOffset] = useState(0), [page, setPage] = useState<WorkflowSearchPage>(), [error, setError] = useState<string>(), [loading, setLoading] = useState(false), [views, setViews] = useState<SavedView[]>(savedViews), [name, setName] = useState("");
 const update = (key: keyof WorkflowFilters, value: string) => { setOffset(0); setFilters(current => ({ ...current, [key]: value, ...(key === "status" ? { active: undefined } : {}) })); };
 useEffect(() => {
  let active = true, controller: AbortController | undefined;
  async function refresh() { controller?.abort(); const request = new AbortController(); controller = request; setLoading(true); try { const result = await searchWorkflows(filters, offset, 25, request.signal); if (active && !request.signal.aborted) { setPage(result); setError(undefined); } } catch (reason) { if (active && !request.signal.aborted) setError(reason instanceof Error ? reason.message : "Could not refresh history."); } finally { if (active && !request.signal.aborted) setLoading(false); } }
  void refresh(); const timer = window.setInterval(() => void refresh(), 10000); return () => { active = false; controller?.abort(); window.clearInterval(timer); };
 }, [filters, offset, refreshToken]);
 function store(next: SavedView[]) { setViews(next); try { localStorage.setItem(storageKey, JSON.stringify(next)); } catch { setError("Saved views could not be stored in this browser."); } }
 return <section className="panel recent-panel" aria-label="Workflow history"><div className="panel-heading"><h2>Recent Runs</h2><span>{page?.totalCount ?? 0} matching workflows</span></div>
  <div className="execution-toolbar"><button type="button" onClick={() => { setFilters({}); setOffset(0); }}>All runs</button><button type="button" onClick={() => { setFilters({ active: "true" }); setOffset(0); }}>Running</button><button type="button" onClick={() => { setFilters({ status: "Failed" }); setOffset(0); }}>Failed</button></div>
  <div className="history-search"><label>Search workflows<input value={filters.search ?? ""} onChange={event => update("search", event.target.value)} placeholder="Issue, repository, ID or failure" /></label></div>
  <details className="history-options"><summary>Filters and saved views</summary>
  <div className="execution-filters history-filters"><label>Workflow status<select aria-label="Workflow status" value={filters.status ?? ""} onChange={event => update("status", event.target.value)}><option value="">All statuses</option>{workflowStates.map(status => <option key={status}>{status}</option>)}</select></label><label>Repository filter<input value={filters.repositoryUrl ?? ""} onChange={event => update("repositoryUrl", event.target.value)} placeholder="Repository URL" /></label><label>Definition filter<select aria-label="Definition filter" value={filters.definitionId ?? ""} onChange={event => update("definitionId", event.target.value)}><option value="">All definitions</option>{definitions.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label><label>Created from<input type="date" value={filters.from?.slice(0, 10) ?? ""} onChange={event => update("from", event.target.value ? `${event.target.value}T00:00:00Z` : "")} /></label><label>Created through<input type="date" value={filters.to?.slice(0, 10) ?? ""} onChange={event => update("to", event.target.value ? `${event.target.value}T23:59:59.999Z` : "")} /></label></div>
  <div className="execution-toolbar"><label>Saved view name<input value={name} onChange={event => setName(event.target.value)} maxLength={60} /></label><button type="button" disabled={!name.trim()} onClick={() => { store([...views.filter(view => view.name !== name.trim()), { name: name.trim(), filters }].slice(-20)); setName(""); }}>Save current filters</button><label>Saved filter views<select aria-label="Saved filter views" value="" onChange={event => { const view = views.find(item => item.name === event.target.value); if (view) { setFilters(view.filters); setOffset(0); } }}><option value="">Choose a saved view</option>{views.map(view => <option key={view.name}>{view.name}</option>)}</select></label>{views.length > 0 && <details><summary>Manage saved views</summary>{views.map(view => <button type="button" key={view.name} onClick={() => store(views.filter(item => item.name !== view.name))}>Delete {view.name}</button>)}</details>}</div>
  </details>
  {error && <p className="error-text" role="alert">{error} Previous history remains visible.</p>}
  <div className="workflow-run-list" aria-label="Workflow runs" aria-busy={loading}>
    {page?.items.map(workflow => { const status = enumName(workflow.status, workflowStates); return <article className={`workflow-run${workflow.workflowId === selectedId ? " selected" : ""}`} key={workflow.workflowId}>
      <button type="button" className="workflow-run-select" onClick={() => onSelect(workflow.workflowId)} aria-label={`Inspect workflow ${workflow.workflowId}`} aria-pressed={workflow.workflowId === selectedId}>
        <span className="workflow-run-heading"><StateBadge value={status} /><span>{duration(workflow.createdAt, ["Completed", "Failed", "Canceled"].includes(status) ? workflow.updatedAt : undefined)}</span></span>
        <strong>{workflow.issueUrl.replace(/^https?:\/\//, "")}</strong>
        <span>{new Date(workflow.createdAt).toLocaleString()}{workflow.isPaused ? " · paused" : ""}{workflow.cancelRequestedAt && !workflow.cancelCompletedAt ? " · canceling" : ""}</span>
        {workflow.failureReason && <span className="workflow-run-failure">{workflow.failureReason}</span>}
      </button>
      <a href={workflow.issueUrl} target="_blank" rel="noreferrer">Open issue ↗</a>
    </article>; })}
    {!page?.items.length && <p>{loading ? "Loading workflows…" : "No workflows match these filters."}</p>}
  </div>
  <div className="execution-toolbar"><button type="button" disabled={offset === 0 || loading} onClick={() => setOffset(value => Math.max(0, value - 25))}>Previous workflows</button><span>{page?.totalCount ? `${offset + 1}–${Math.min(offset + 25, page.totalCount)} of ${page.totalCount}` : "0 workflows"}</span><button type="button" disabled={loading || !page || offset + 25 >= page.totalCount} onClick={() => setOffset(value => value + 25)}>Next workflows</button></div>
 </section>;
}
