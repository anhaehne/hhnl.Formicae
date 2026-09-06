import type { Persona, PersonaSnapshot } from "../api";

export function PersonaPicker({ label, value, inheritedId, personas, savedSnapshot, disabled, onChange }: {
  label: string; value?: string | null; inheritedId?: string | null; personas: Persona[]; savedSnapshot?: PersonaSnapshot | null;
  disabled: boolean; onChange: (id: string | undefined) => void;
}) {
  const task = inheritedId !== undefined;
  const effectiveId = value || inheritedId || "default";
  const current = personas.find(persona => persona.id === effectiveId);
  const saved = savedSnapshot?.id === effectiveId ? savedSnapshot : undefined;
  const preview = current || saved;
  return <div className="persona-picker">
    <label><span>{label}</span><select aria-label={label} disabled={disabled} value={!task && value === "default" ? "" : value || ""} onChange={event => onChange(event.target.value || undefined)}>
      <option value="">{task ? "Inherit workflow persona" : "Default behavior"}</option>
      {task && <option value="default">Default behavior</option>}
      {value && !personas.some(persona => persona.id === value) && value !== "default" && <option value={value}>Unavailable: {savedSnapshot?.name || value}</option>}
      {personas.filter(persona => !persona.builtIn).map(persona => <option key={persona.id} value={persona.id}>{persona.name} · revision {persona.revision}</option>)}
    </select></label>
    {saved && current && saved.revision !== current.revision && <p className="persona-revision-notice">Saved version uses revision {saved.revision}; Save Version will use revision {current.revision}.</p>}
    {!current && personas.length > 0 && effectiveId !== "default" && <p className="persona-revision-notice">{saved ? "The saved version remains runnable with its recorded persona. " : "This persona is unavailable. "}A new enabled version needs an active persona selection.</p>}
    {saved && <p className="muted">Saved persona: {saved.name} · revision {saved.revision}</p>}
    {effectiveId === "default" ? <p className="muted">Default behavior adds no persona instructions.</p> : preview ? <details className="optional-settings persona-preview"><summary>{current ? `Preview for next save · revision ${current.revision}` : `Saved context · revision ${preview.revision}`}</summary>
      {(["instructions", "tone", "operatingConstraints"] as const).map(key => preview[key] ? <div key={key}><strong>{key === "operatingConstraints" ? "Operating constraints" : key === "instructions" ? "Instructions" : "Tone"}</strong><p>{preview[key]}</p></div> : null)}
      {saved && current && saved.revision !== current.revision && <details><summary>Saved revision {saved.revision} context</summary><p>{saved.instructions}</p><p>{saved.tone}</p><p>{saved.operatingConstraints}</p></details>}
    </details> : null}
  </div>;
}
