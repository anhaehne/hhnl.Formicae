import { useEffect, useRef, useState } from "react";
import { AiSettings, getModelDiscovery, ModelDiscoveryStatus, startModelDiscovery } from "./api";

export function DefaultModelSelect({ settings, model, runtimeSaved, disabled, onChange }: {
  settings?: AiSettings;
  model: string;
  runtimeSaved: boolean;
  disabled: boolean;
  onChange: (model: string) => void;
}) {
  const [catalog, setCatalog] = useState<ModelDiscoveryStatus>();
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const generation = useRef(0);
  const settingsId = settings?.id;
  const supported = settings?.authMethod === "CodexSubscription"
    && (settings.agentKind !== "Acp" || settings.acpProvider === "Codex");

  useEffect(() => {
    generation.current++;
    setCatalog(undefined);
    setError("");
    setBusy(false);
    return () => { generation.current++; };
  }, [settingsId, runtimeSaved, supported]);

  async function discover() {
    if (!settingsId || !runtimeSaved || !supported) return;
    const current = ++generation.current;
    setBusy(true);
    setError("");
    const deadline = Date.now() + 125_000;
    try {
      let result = await startModelDiscovery(settingsId);
      while (result.status === "Running" && result.jobName) {
        if (generation.current !== current) return;
        if (Date.now() > deadline) throw new Error("Discovery timed out. Retry after checking the worker and authentication.");
        await new Promise(resolve => setTimeout(resolve, 1500));
        if (generation.current !== current) return;
        result = await getModelDiscovery(settingsId, result.jobName);
      }
      if (generation.current === current) setCatalog(result);
    } catch (failure) {
      if (generation.current === current) setError(failure instanceof Error ? failure.message : "Model discovery failed.");
    } finally {
      if (generation.current === current) setBusy(false);
    }
  }

  return <>
    <label>
      <span>Default Model</span>
      <select value={model} disabled={disabled || busy} onChange={event => onChange(event.target.value)}>
        <option value="">Use runtime default</option>
        {model && !catalog?.models.some(item => item.id === model) ? <option value={model}>{model} (saved selection)</option> : null}
        {catalog?.models.map(item => <option key={item.id} value={item.id}>{item.displayName}{item.isDefault ? " (CLI default)" : ""}</option>)}
      </select>
    </label>
    <p className="muted">Used when a workflow or step does not select a model.</p>
    {!settings ? <p className="muted">Save this AI before discovering models.</p>
      : !runtimeSaved ? <p className="muted">Save the runtime and authentication settings before discovering models.</p>
      : supported ? <button type="button" className="secondary-button" onClick={discover} disabled={disabled || busy}>{busy ? "Discovering models…" : "Discover / refresh models"}</button>
      : <p className="muted">CLI model discovery is not supported for this configuration.</p>}
    {catalog?.status === "Succeeded" && catalog.models.length === 0 ? <p className="muted">The CLI returned no models.</p> : null}
    {catalog?.failureReason ? <p role="alert">{catalog.failureReason}</p> : null}
    {error ? <p role="alert">{error}</p> : null}
  </>;
}
