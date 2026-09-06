import { useEffect, useRef, useState } from "react";
import { useBeforeUnload, useBlocker } from "react-router-dom";
import { ApiError, createPersona, deletePersona, listPersonas, updatePersona, type Persona, type PersonaInput } from "./api";

const empty: PersonaInput = { name: "", instructions: "", tone: "", operatingConstraints: "" };
const formFor = (persona: Persona): PersonaInput => ({ name: persona.name, instructions: persona.instructions, tone: persona.tone, operatingConstraints: persona.operatingConstraints });
export default function PersonasPage({ canAdminister }: { canAdminister: boolean }) {
  const [personas, setPersonas] = useState<Persona[]>([]), [selected, setSelected] = useState<Persona>();
  const [form, setForm] = useState<PersonaInput>(empty), [baseline, setBaseline] = useState<PersonaInput>(empty);
  const [loading, setLoading] = useState(true);
  const [query, setQuery] = useState(""), [busy, setBusy] = useState(false), [error, setError] = useState(""), [notice, setNotice] = useState("");
  const [conflict, setConflict] = useState(false), [confirmDelete, setConfirmDelete] = useState(false), [pending, setPending] = useState<(() => void)>();
  const dialog = useRef<HTMLDialogElement>(null);
  const dirty = JSON.stringify(form) !== JSON.stringify(baseline), editable = canAdminister && !selected?.builtIn && !busy && !loading;
  const blocker = useBlocker(dirty && canAdminister);
  useBeforeUnload(event => { if (dirty && canAdminister) { event.preventDefault(); event.returnValue = ""; } });
  const choose = (persona?: Persona) => { setSelected(persona); const next = persona ? formFor(persona) : empty; setForm(next); setBaseline(next); setError(""); setNotice(""); setConflict(false); };
  const guard = (action: () => void) => { if (dirty) setPending(() => action); else action(); };
  async function refresh() { try { const items = await listPersonas(); setPersonas(items); return items; } catch (failure) { setError(failure instanceof Error ? failure.message : "Could not load personas."); return undefined; } }
  useEffect(() => { let active = true; listPersonas().then(items => { if (active) { setPersonas(items); choose(items[0]); } }).catch(failure => { if (active) setError(String(failure)); }).finally(() => { if (active) setLoading(false); }); return () => { active = false; }; }, []);
  const showDialog = confirmDelete || !!pending || blocker.state === "blocked";
  useEffect(() => { if (showDialog) dialog.current?.showModal(); }, [showDialog]);
  const cancel = () => { setConfirmDelete(false); setPending(undefined); if (blocker.state === "blocked") blocker.reset(); };
  async function save(event: React.FormEvent) {
    event.preventDefault(); if (!editable || conflict) return; setBusy(true); setError(""); setNotice("");
    try { const saved = selected ? await updatePersona(selected.id, form, selected.revision) : await createPersona(form); choose(saved); setNotice("Persona saved."); await refresh(); }
    catch (failure) { setError(failure instanceof Error ? failure.message : "Could not save persona."); setConflict(failure instanceof ApiError && failure.status === 409); }
    finally { setBusy(false); }
  }
  async function remove() {
    if (!selected || !editable) return; setConfirmDelete(false); setBusy(true); setError("");
    try { await deletePersona(selected.id, selected.revision); choose(); await refresh(); setNotice("Persona deleted. Saved workflow versions keep their recorded context."); }
    catch (failure) { setError(failure instanceof Error ? failure.message : "Could not delete persona."); setConflict(failure instanceof ApiError && failure.status === 409); }
    finally { setBusy(false); }
  }
  return <section className="personas-workspace">
    <aside className="panel persona-catalog" aria-label="Persona catalog"><div className="panel-heading"><h2>Personas</h2><button type="button" className="secondary-button" disabled={busy || loading || !canAdminister} onClick={() => guard(() => choose())}>New persona</button></div>
      <label><span>Search personas</span><input value={query} onChange={event => setQuery(event.target.value)} /></label>
      <button type="button" className="secondary-button" disabled={busy} onClick={() => void refresh()}>Refresh personas</button>
      <div className="persona-list">{personas.filter(persona => persona.name.toLowerCase().includes(query.toLowerCase())).map(persona => <button type="button" aria-pressed={selected?.id === persona.id} key={persona.id} disabled={busy} onClick={() => guard(() => choose(persona))}><strong>{persona.name}</strong><span>{persona.builtIn ? "Built-in · " : ""}Revision {persona.revision}</span></button>)}</div>
    </aside>
    <form className="panel persona-form" onSubmit={save}><div className="panel-heading"><h2>{selected ? selected.name : "New persona"}</h2><span className="muted">{selected ? `Revision ${selected.revision}` : "Unsaved"}{dirty ? " · Unsaved changes" : ""}</span></div>
      {selected?.builtIn && <p className="muted">The built-in default preserves existing agent behavior and cannot be edited or deleted.</p>}
      <p className="muted">Personas add plain-text instructions. They do not grant tools, permissions, or model overrides.</p>
      <label><span>Persona name</span><input aria-label="Persona name" disabled={!editable} value={form.name} maxLength={120} required onChange={event => setForm({ ...form, name: event.target.value })} /></label>
      {([['instructions', 'Instructions', 16000], ['tone', 'Tone', 1000], ['operatingConstraints', 'Operating constraints', 8000]] as const).map(([key, label, max]) => <label key={key}><span>{label}</span><textarea aria-label={label} rows={key === "tone" ? 2 : 5} disabled={!editable} value={form[key]} maxLength={max} onChange={event => setForm({ ...form, [key]: event.target.value })} /></label>)}
      {error && <p role="alert" className="error-text">{error}</p>}{notice && <p role="status" className="success-text">{notice}</p>}
      {conflict && <div className="persona-conflict"><p>Your edits are retained. Another operator changed this persona. Reload the current revision before saving again.</p><button type="button" className="secondary-button" disabled={busy} onClick={() => guard(() => { setBusy(true); void refresh().then(items => { if (items) choose(items.find(persona => persona.id === selected?.id)); }).finally(() => setBusy(false)); })}>Reload current revision</button></div>}
      <div className="persona-form-actions"><button type="submit" className="primary-button" disabled={!editable || conflict || !form.name.trim()}>{busy ? "Saving…" : "Save persona"}</button><button type="button" className="secondary-button" disabled={busy || !dirty} onClick={() => guard(() => choose(selected))}>Cancel edits</button>{selected && !selected.builtIn && <button type="button" className="secondary-button danger-button" disabled={!editable || conflict} onClick={() => setConfirmDelete(true)}>Delete persona</button>}</div>
    </form>
    {showDialog && <dialog className="editor-confirm" ref={dialog} aria-label={confirmDelete ? "Delete persona" : "Discard persona edits"} onCancel={event => { event.preventDefault(); cancel(); }}><div className="editor-confirm-content"><h2>{confirmDelete ? "Delete persona?" : "Discard unsaved edits?"}</h2><p>{confirmDelete ? "This persona will be unavailable for future selection. Existing workflow versions retain their saved persona context." : "Your changes since the last save will be lost."}</p></div><div className="editor-confirm-actions"><button type="button" className="secondary-button" autoFocus onClick={cancel}>Cancel</button><button type="button" className="primary-button editor-discard-button" onClick={() => { if (confirmDelete) void remove(); else if (pending) { const action = pending; setPending(undefined); action(); } else if (blocker.state === "blocked") blocker.proceed(); }}>{confirmDelete ? "Delete" : "Discard"}</button></div></dialog>}
  </section>;
}
