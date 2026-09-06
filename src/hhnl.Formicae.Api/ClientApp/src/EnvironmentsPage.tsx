import { useEffect, useRef, useState } from "react";
import { useBeforeUnload, useBlocker } from "react-router-dom";
import { ApiError, createEnvironment, deleteEnvironment, listEnvironments, updateEnvironment, type EnvironmentProfile, type EnvironmentInput } from "./api";

const empty: EnvironmentInput = { name: "", description: "", configuration: { schemaVersion: 1, runtime: null, image: null, tools: [], mcpServers: [] } };
const formFor = (persona: EnvironmentProfile): EnvironmentInput => ({ name: persona.name, description: persona.description, configuration: persona.configuration });
export default function EnvironmentsPage({ canAdminister }: { canAdminister: boolean }) {
  const [personas, setPersonas] = useState<EnvironmentProfile[]>([]), [selected, setSelected] = useState<EnvironmentProfile>();
  const [form, setForm] = useState<EnvironmentInput>(empty), [baseline, setBaseline] = useState<EnvironmentInput>(empty);
  const [loading, setLoading] = useState(true);
  const [query, setQuery] = useState(""), [busy, setBusy] = useState(false), [error, setError] = useState(""), [notice, setNotice] = useState("");
  const [conflict, setConflict] = useState(false), [confirmDelete, setConfirmDelete] = useState(false), [pending, setPending] = useState<(() => void)>();
  const dialog = useRef<HTMLDialogElement>(null);
  const dirty = JSON.stringify(form) !== JSON.stringify(baseline), editable = canAdminister && !selected?.builtIn && !busy && !loading;
  const blocker = useBlocker(dirty && canAdminister);
  useBeforeUnload(event => { if (dirty && canAdminister) { event.preventDefault(); event.returnValue = ""; } });
  const choose = (persona?: EnvironmentProfile) => { setSelected(persona); const next = persona ? formFor(persona) : empty; setForm(next); setBaseline(next); setError(""); setNotice(""); setConflict(false); };
  const guard = (action: () => void) => { if (dirty) setPending(() => action); else action(); };
  async function refresh() { try { const items = await listEnvironments(); setPersonas(items); return items; } catch (failure) { setError(failure instanceof Error ? failure.message : "Could not load environments."); return undefined; } }
  useEffect(() => { let active = true; listEnvironments().then(items => { if (active) { setPersonas(items); choose(items[0]); } }).catch(failure => { if (active) setError(String(failure)); }).finally(() => { if (active) setLoading(false); }); return () => { active = false; }; }, []);
  const showDialog = confirmDelete || !!pending || blocker.state === "blocked";
  useEffect(() => { if (showDialog) dialog.current?.showModal(); }, [showDialog]);
  const cancel = () => { setConfirmDelete(false); setPending(undefined); if (blocker.state === "blocked") blocker.reset(); };
  async function save(event: React.FormEvent) {
    event.preventDefault(); if (!editable || conflict) return; setBusy(true); setError(""); setNotice("");
    try { const saved = selected ? await updateEnvironment(selected.id, form, selected.revision) : await createEnvironment(form); choose(saved); setNotice("Environment saved."); await refresh(); }
    catch (failure) { setError(failure instanceof Error ? failure.message : "Could not save environment."); setConflict(failure instanceof ApiError && failure.status === 409); }
    finally { setBusy(false); }
  }
  async function remove() {
    if (!selected || !editable) return; setConfirmDelete(false); setBusy(true); setError("");
    try { await deleteEnvironment(selected.id, selected.revision); choose(); await refresh(); setNotice("Environment deleted. Saved workflow versions keep their recorded configuration."); }
    catch (failure) { setError(failure instanceof Error ? failure.message : "Could not delete environment."); setConflict(failure instanceof ApiError && failure.status === 409); }
    finally { setBusy(false); }
  }
  return <section className="personas-workspace">
    <aside className="panel persona-catalog" aria-label="Environment catalog"><div className="panel-heading"><h2>Environments</h2><button type="button" className="secondary-button" disabled={busy || loading || !canAdminister} onClick={() => guard(() => choose())}>New environment</button></div>
      <label><span>Search environments</span><input value={query} onChange={event => setQuery(event.target.value)} /></label>
      <button type="button" className="secondary-button" disabled={busy} onClick={() => void refresh()}>Refresh environments</button>
      <div className="persona-list">{personas.filter(persona => persona.name.toLowerCase().includes(query.toLowerCase())).map(persona => <button type="button" aria-pressed={selected?.id === persona.id} key={persona.id} disabled={busy} onClick={() => guard(() => choose(persona))}><strong>{persona.name}</strong><span>{persona.builtIn ? "Built-in · " : ""}Revision {persona.revision}</span></button>)}</div>
    </aside>
    <form className="panel persona-form" onSubmit={save}><div className="panel-heading"><h2>{selected ? selected.name : "New environment"}</h2><span className="muted">{selected ? `Revision ${selected.revision}` : "Unsaved"}{dirty ? " · Unsaved changes" : ""}</span></div>
      {selected?.builtIn && <p className="muted">The built-in default preserves existing runtime behavior and cannot be edited or deleted.</p>}
      <p className="muted">Environment profiles set task runtime limits. The default leaves existing runtime settings unchanged.</p>
      <label><span>Environment name</span><input aria-label="Environment name" disabled={!editable} value={form.name} maxLength={120} required onChange={event => setForm({ ...form, name: event.target.value })} /></label>
      <label><span>Description</span><textarea aria-label="Description" rows={3} disabled={!editable} value={form.description} maxLength={2000} onChange={event => setForm({ ...form, description: event.target.value })} /></label>
      <label className="toggle-label"><input aria-label="Limit task runtime" type="checkbox" disabled={!editable} checked={form.configuration.runtime?.timeoutLimitSeconds != null} onChange={event => setForm({ ...form, configuration: { ...form.configuration, runtime: event.target.checked ? { timeoutLimitSeconds: 1800 } : null } })} /><span>Limit task runtime</span></label>
      {form.configuration.runtime?.timeoutLimitSeconds != null && <label><span>Maximum task runtime (seconds)</span><input aria-label="Maximum task runtime (seconds)" type="number" min={1} max={3600} step={1} required disabled={!editable} value={form.configuration.runtime.timeoutLimitSeconds || ""} onChange={event => setForm({ ...form, configuration: { ...form.configuration, runtime: { timeoutLimitSeconds: Number(event.target.value) } } })} /></label>}
      <p className="muted">{form.configuration.runtime?.timeoutLimitSeconds != null ? "This cap can shorten each AI task's existing timeout and never increases it. Choose 1–3,600 seconds." : "Inherit each task's existing runtime timeout."} This selection applies to AI tasks, including parallel Plan branches. It does not configure direct pull-request actions or control nodes.</p>
      <p className="muted">Saved versions pin this profile configuration. Deployment image tags and platform settings remain platform-managed.</p>
      {error && <p role="alert" className="error-text">{error}</p>}{notice && <p role="status" className="success-text">{notice}</p>}
      {conflict && <div className="persona-conflict"><p>Your edits are retained. Another operator changed this environment. Reload the current revision before saving again.</p><button type="button" className="secondary-button" disabled={busy} onClick={() => guard(() => { setBusy(true); void refresh().then(items => { if (items) choose(items.find(persona => persona.id === selected?.id)); }).finally(() => setBusy(false)); })}>Reload current revision</button></div>}
      <div className="persona-form-actions"><button type="submit" className="primary-button" disabled={!editable || conflict || !form.name.trim()}>{busy ? "Saving…" : "Save environment"}</button><button type="button" className="secondary-button" disabled={busy || !dirty} onClick={() => guard(() => choose(selected))}>Cancel edits</button>{selected && !selected.builtIn && <button type="button" className="secondary-button danger-button" disabled={!editable || conflict} onClick={() => setConfirmDelete(true)}>Delete environment</button>}</div>
    </form>
    {showDialog && <dialog className="editor-confirm" ref={dialog} aria-label={confirmDelete ? "Delete environment" : "Discard environment edits"} onCancel={event => { event.preventDefault(); cancel(); }}><div className="editor-confirm-content"><h2>{confirmDelete ? "Delete environment?" : "Discard unsaved edits?"}</h2><p>{confirmDelete ? "This environment will be unavailable for future selection. Existing workflow versions retain their saved environment configuration." : "Your changes since the last save will be lost."}</p></div><div className="editor-confirm-actions"><button type="button" className="secondary-button" autoFocus onClick={cancel}>Cancel</button><button type="button" className="primary-button editor-discard-button" onClick={() => { if (confirmDelete) void remove(); else if (pending) { const action = pending; setPending(undefined); action(); } else if (blocker.state === "blocked") blocker.proceed(); }}>{confirmDelete ? "Delete" : "Discard"}</button></div></dialog>}
  </section>;
}
