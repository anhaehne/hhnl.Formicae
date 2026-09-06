import { useEffect, useRef, useState } from "react";
import { AiSettings, getAiSettings, getModelDiscovery, ModelDiscoveryStatus, startModelDiscovery } from "./api";

export function StepModelSettings({ aiSettingsId, model, disabled, onChange }: {
  aiSettingsId?: string | null;
  model?: string | null;
  disabled: boolean;
  onChange: (settings: { aiSettingsId?: string; model?: string }) => void;
}) {
  const [settings, setSettings] = useState<AiSettings[]>([]);
  const [catalog, setCatalog] = useState<ModelDiscoveryStatus>();
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const generation = useRef(0);
  const effectiveId = aiSettingsId || settings[0]?.id || "default";
  const selected = settings.find(item => item.id === effectiveId);
  const usesCodexCli = (item: AiSettings) => item.authMethod === "CodexSubscription" && (item.agentKind !== "Acp" || item.acpProvider === "Codex");
  const supported = selected !== undefined && usesCodexCli(selected);

  useEffect(() => {
    let active = true;
    getAiSettings().then(items => { if (active) setSettings(items); }).catch(() => { if (active) setError("Could not load AI configurations."); });
    return () => { active = false; };
  }, []);

  useEffect(() => {
    generation.current++;
    setCatalog(undefined);
    setError("");
    setBusy(false);
    return () => { generation.current++; };
  }, [effectiveId]);

  async function discover() {
    const current = ++generation.current;
    setBusy(true);
    setError("");
    const deadline = Date.now() + 125_000;
    try {
      let result = await startModelDiscovery(effectiveId);
      while (result.status === "Running" && result.jobName) {
        if (generation.current !== current) return;
        if (Date.now() > deadline) throw new Error("Discovery timed out. Retry after checking the worker and authentication.");
        await new Promise(resolve => setTimeout(resolve, 1500));
        if (generation.current !== current) return;
        result = await getModelDiscovery(effectiveId, result.jobName);
      }
      if (generation.current === current) setCatalog(result);
    } catch (failure) {
      if (generation.current === current) setError(failure instanceof Error ? failure.message : "Model discovery failed.");
    } finally {
      if (generation.current === current) setBusy(false);
    }
  }

  return <>
    <label><span>AI configuration</span>
      <select value={aiSettingsId || ""} disabled={disabled} onChange={event => onChange({ aiSettingsId: event.target.value || undefined, model: undefined })}>
        <option value="">Default AI configuration</option>
        {aiSettingsId && !settings.some(item => item.id === aiSettingsId) ? <option value={aiSettingsId}>{aiSettingsId} (unavailable)</option> : null}
        {settings.map(item => <option key={item.id} value={item.id} disabled={item.agentKind === "Acp" && !usesCodexCli(item)}>{item.name}{item.agentKind === "Acp" && !usesCodexCli(item) ? " (execution unsupported)" : ""}</option>)}
      </select>
    </label>
    <label><span>Step model</span>
      <select value={model || ""} disabled={disabled || busy} onChange={event => onChange({ aiSettingsId: aiSettingsId || undefined, model: event.target.value || undefined })}>
        <option value="">Inherit workflow model</option>
        {model && !catalog?.models.some(item => item.id === model) ? <option value={model}>{model} (saved selection)</option> : null}
        {catalog?.models.map(item => <option key={item.id} value={item.id}>{item.displayName}{item.isDefault ? " (CLI default)" : ""}</option>)}
      </select>
    </label>
    <p className="muted">An unset step model uses the workflow model, then the configuration default.</p>
    {supported ? <button type="button" className="secondary-button" onClick={discover} disabled={disabled || busy}>{busy ? "Discovering models…" : "Discover / refresh models"}</button>
      : selected ? <p className="muted">CLI model discovery is not supported for this configuration.</p> : null}
    {catalog?.status === "Succeeded" && catalog.models.length === 0 ? <p className="muted">The CLI returned no models.</p> : null}
    {catalog?.failureReason ? <p role="alert">{catalog.failureReason}</p> : null}
    {error ? <p role="alert">{error}</p> : null}
  </>;
}
