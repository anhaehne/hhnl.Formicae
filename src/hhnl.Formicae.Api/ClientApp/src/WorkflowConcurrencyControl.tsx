import { useEffect, useState } from "react";
import { getWorkflowConcurrency, getWorkflowDefinition, setWorkflowConcurrency } from "./api";

export default function WorkflowConcurrencyControl({ definitionId, initialLimit = null, canAdminister }: {
  definitionId?: string; initialLimit?: number | null; canAdminister: boolean;
}) {
  const [value, setValue] = useState(initialLimit?.toString() ?? ""), [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false), [error, setError] = useState(""), [notice, setNotice] = useState("");
  useEffect(() => {
    let active = true;
    (definitionId ? getWorkflowDefinition(definitionId) : getWorkflowConcurrency()).then(settings => { if (active) setValue(settings.maxActiveInstances?.toString() ?? ""); })
      .catch(reason => { if (active) setError(reason instanceof Error ? reason.message : "Could not load limit."); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [definitionId]);
  async function save() {
    const limit = value === "" ? null : Number(value);
    if (limit !== null && (!Number.isSafeInteger(limit) || limit < 1 || limit > 2147483647)) {
      setError("Enter a positive whole number, or leave empty for unlimited."); return;
    }
    setBusy(true); setError(""); setNotice("");
    try { await setWorkflowConcurrency(limit, definitionId); setNotice("Instance limit saved."); }
    catch (reason) { setError(reason instanceof Error ? reason.message : "Could not save limit."); }
    finally { setBusy(false); }
  }
  return <section aria-label={definitionId ? "Workflow instance limit" : "Global workflow instance limit"} className={definitionId ? "editor-property-section" : "panel"}>
    <label><span>{definitionId ? "Maximum active instances" : "Global maximum active instances"}</span>
      <input type="number" min="1" step="1" placeholder="Unlimited" value={value} disabled={!canAdminister || busy || loading}
        onChange={event => { setValue(event.target.value); setNotice(""); }} /></label>
    <p className="muted">{definitionId ? "Applies to all versions of this workflow. " : "Applies across all workflows. "}Instances waiting for triggers do not count. Leave empty for unlimited. Existing work finishes when a limit is reduced.</p>
    <button type="button" className="secondary-button" disabled={!canAdminister || busy || loading} onClick={() => void save()}>{busy ? "Saving…" : "Save instance limit"}</button>
    {error && <p role="alert">{error}</p>}{notice && <p role="status">{notice}</p>}
  </section>;
}
