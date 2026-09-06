import { CustomTaskSchema } from "./workflowEditor/CustomTaskSchema";
import { useEffect, useRef, useState } from "react";
import { useBeforeUnload, useBlocker } from "react-router-dom";
import { ApiError, createCustomTask, deleteCustomTask, listCustomTasks, updateCustomTask, type CustomTaskDefinition, type CustomTaskInput } from "./api";

const empty: CustomTaskInput = { name: "", description: "", promptTemplate: "", inputs: [], runner: { kind: "agent", timeoutSeconds: 1800 } };
const formFor = (persona: CustomTaskDefinition): CustomTaskInput => ({ name: persona.name, description: persona.description, promptTemplate: persona.promptTemplate, inputs: persona.inputs, runner: persona.runner });
export default function CustomTasksPage({ canAdminister }: { canAdminister: boolean }) {
  const [personas, setPersonas] = useState<CustomTaskDefinition[]>([]), [selected, setSelected] = useState<CustomTaskDefinition>();
  const [form, setForm] = useState<CustomTaskInput>(empty), [baseline, setBaseline] = useState<CustomTaskInput>(empty);
  const [loading, setLoading] = useState(true);
  const [query, setQuery] = useState(""), [busy, setBusy] = useState(false), [error, setError] = useState(""), [notice, setNotice] = useState("");
  const [conflict, setConflict] = useState(false), [confirmDelete, setConfirmDelete] = useState(false), [pending, setPending] = useState<(() => void)>();
  const dialog = useRef<HTMLDialogElement>(null);
  const dirty = JSON.stringify(form) !== JSON.stringify(baseline), editable = canAdminister && !busy && !loading;
  const blocker = useBlocker(dirty && canAdminister);
  useBeforeUnload(event => { if (dirty && canAdminister) { event.preventDefault(); event.returnValue = ""; } });
  const choose = (persona?: CustomTaskDefinition) => { setSelected(persona); const next = persona ? formFor(persona) : empty; setForm(next); setBaseline(next); setError(""); setNotice(""); setConflict(false); };
  const guard = (action: () => void) => { if (dirty) setPending(() => action); else action(); };
  async function refresh() { try { const items = await listCustomTasks(); setPersonas(items); return items; } catch (failure) { setError(failure instanceof Error ? failure.message : "Could not load custom tasks."); return undefined; } }
  useEffect(() => { let active = true; listCustomTasks().then(items => { if (active) { setPersonas(items); choose(items[0]); } }).catch(failure => { if (active) setError(String(failure)); }).finally(() => { if (active) setLoading(false); }); return () => { active = false; }; }, []);
  const showDialog = confirmDelete || !!pending || blocker.state === "blocked";
  useEffect(() => { if (showDialog) dialog.current?.showModal(); }, [showDialog]);
  const cancel = () => { setConfirmDelete(false); setPending(undefined); if (blocker.state === "blocked") blocker.reset(); };
  async function save(event: React.FormEvent) {
    event.preventDefault(); if (!editable || conflict) return; setBusy(true); setError(""); setNotice("");
    try { const saved = selected ? await updateCustomTask(selected.id, form, selected.revision) : await createCustomTask(form); choose(saved); setNotice("Custom task saved."); await refresh(); }
    catch (failure) { setError(failure instanceof Error ? failure.message : "Could not save custom task."); setConflict(failure instanceof ApiError && failure.status === 409); }
    finally { setBusy(false); }
  }
  async function remove() {
    if (!selected || !editable) return; setConfirmDelete(false); setBusy(true); setError("");
    try { await deleteCustomTask(selected.id, selected.revision); choose(); await refresh(); setNotice("Custom task deleted. Saved workflow versions keep their recorded context."); }
    catch (failure) { setError(failure instanceof Error ? failure.message : "Could not delete custom task."); setConflict(failure instanceof ApiError && failure.status === 409); }
    finally { setBusy(false); }
  }
  return <section className="personas-workspace">
    <aside className="panel persona-catalog" aria-label="Custom task catalog"><div className="panel-heading"><h2>Custom tasks</h2><button type="button" className="secondary-button" disabled={busy || loading || !canAdminister} onClick={() => guard(() => choose())}>New custom task</button></div>
      <label><span>Search custom tasks</span><input value={query} onChange={event => setQuery(event.target.value)} /></label>
      <button type="button" className="secondary-button" disabled={busy} onClick={() => void refresh()}>Refresh custom tasks</button>
      <div className="persona-list">{personas.filter(persona => persona.name.toLowerCase().includes(query.toLowerCase())).map(persona => <button type="button" aria-pressed={selected?.id === persona.id} key={persona.id} disabled={busy} onClick={() => guard(() => choose(persona))}><strong>{persona.name}</strong><span>Revision {persona.revision}</span></button>)}</div>
    </aside>
    <form className="panel persona-form custom-task-form" onSubmit={save}><div className="panel-heading"><h2>{selected ? selected.name : "New custom task"}</h2><span className="muted">{selected ? `Revision ${selected.revision}` : "Unsaved"}{dirty ? " · Unsaved changes" : ""}</span></div>
      <p className="muted">Reusable agent tasks run in a scratch workspace. They do not automatically check out repositories, commit changes, or create pull requests.</p>
      <label><span>Task name</span><input aria-label="Task name" disabled={!editable} value={form.name} maxLength={120} required onChange={event => setForm({ ...form, name: event.target.value })} /></label>
      <label><span>Description</span><textarea aria-label="Description" disabled={!editable} value={form.description} maxLength={2000} onChange={event => setForm({ ...form, description: event.target.value })} /></label>
      <label><span>Prompt template</span><textarea aria-label="Prompt template" rows={6} required disabled={!editable} value={form.promptTemplate} maxLength={16000} onChange={event => setForm({ ...form, promptTemplate: event.target.value })} /></label>
      <p className="muted">Use {'{{input.NAME}}'} for declared inputs. Workflow fields: issueUrl, repositoryUrl, baseBranch, model, planArtifact, pullRequestUrl (for example {'{{workflow.issueUrl}}'}). Values are inserted once; expressions and filters are unavailable.</p>
      <CustomTaskSchema inputs={form.inputs} disabled={!editable} onChange={inputs => setForm({ ...form, inputs })} />
      <fieldset><legend>Agent runner</legend><label><span>Timeout seconds</span><input aria-label="Timeout seconds" type="number" min={1} max={3600} required disabled={!editable} value={form.runner.timeoutSeconds} onChange={event => setForm({ ...form, runner: { kind: "agent", timeoutSeconds: Number(event.target.value) } })} /></label></fieldset>
      {error && <p role="alert" className="error-text">{error}</p>}{notice && <p role="status" className="success-text">{notice}</p>}
      {conflict && <div className="persona-conflict"><p>Your edits are retained. Another operator changed this custom task. Reload the current revision before saving again.</p><button type="button" className="secondary-button" disabled={busy} onClick={() => guard(() => { setBusy(true); void refresh().then(items => { if (items) choose(items.find(persona => persona.id === selected?.id)); }).finally(() => setBusy(false)); })}>Reload current revision</button></div>}
      <div className="persona-form-actions"><button type="submit" className="primary-button" disabled={!editable || conflict || !form.name.trim()}>{busy ? "Saving…" : "Save custom task"}</button><button type="button" className="secondary-button" disabled={busy || !dirty} onClick={() => guard(() => choose(selected))}>Cancel edits</button>{selected && <button type="button" className="secondary-button danger-button" disabled={!editable || conflict} onClick={() => setConfirmDelete(true)}>Delete custom task</button>}</div>
    </form>
    {showDialog && <dialog className="editor-confirm" ref={dialog} aria-label={confirmDelete ? "Delete custom task" : "Discard custom task edits"} onCancel={event => { event.preventDefault(); cancel(); }}><div className="editor-confirm-content"><h2>{confirmDelete ? "Delete custom task?" : "Discard unsaved edits?"}</h2><p>{confirmDelete ? "This custom task will be unavailable for future selection. Existing workflow versions retain their saved task definition." : "Your changes since the last save will be lost."}</p></div><div className="editor-confirm-actions"><button type="button" className="secondary-button" autoFocus onClick={cancel}>Cancel</button><button type="button" className="primary-button editor-discard-button" onClick={() => { if (confirmDelete) void remove(); else if (pending) { const action = pending; setPending(undefined); action(); } else if (blocker.state === "blocked") blocker.proceed(); }}>{confirmDelete ? "Delete" : "Discard"}</button></div></dialog>}
  </section>;
}
