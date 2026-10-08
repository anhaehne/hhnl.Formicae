import { useEffect, useState } from "react";
import { getIntegration, listIntegrations, type IntegrationDetail, type WorkflowEventSettings } from "../api";
import { eventDefinition } from "../workflowEvents";

export function EventSettings({ uses, value, disabled, webhookUrl, onChange }: { uses: string; value: WorkflowEventSettings; disabled: boolean; webhookUrl?: string; onChange: (value: WorkflowEventSettings) => void }) {
  const definition = eventDefinition(uses);
  const [integrations, setIntegrations] = useState<IntegrationDetail[]>([]);
  const [error, setError] = useState("");
  useEffect(() => {
    if (!definition?.fields.some(field => field.kind === "repositories")) return;
    let canceled = false;
    listIntegrations().then(items => Promise.all(items.map(item => getIntegration(item.id)))).then(items => {
      if (!canceled) setIntegrations(items.filter(item => !definition.provider || item.providerType === definition.provider));
    }).catch(() => { if (!canceled) setError("Could not load connected repositories. Reopen this inspector to retry."); });
    return () => { canceled = true; };
  }, [uses]);
  const update = (name: string, fieldValue: unknown) => onChange({ ...value, [name]: fieldValue });
  return <section className="editor-property-section"><h4>Event settings</h4>
    {definition?.manual && <p className="muted">Default entrypoint for manual runs. Remove this event for a workflow with external events only.</p>}
    <label className="toggle-label"><input type="checkbox" checked={value.enabled} disabled={disabled} onChange={event => update("enabled", event.target.checked)} /><span>Event enabled</span></label>
    {definition?.fields.map(field => field.kind === "repositories" ? <div key={field.name}>
      {error && <p role="alert">{error}</p>}
      {integrations.map(integration => <fieldset key={integration.id}><legend>{integration.displayName}</legend>{integration.repositories.map(repository => <label className="toggle-label" key={repository.id}>
        <input type="checkbox" disabled={disabled} checked={(value.repositoryIds ?? []).includes(repository.id)} onChange={event => update(field.name, event.target.checked ? [...(value.repositoryIds ?? []), repository.id] : (value.repositoryIds ?? []).filter(id => id !== repository.id))} /><span>{repository.owner}/{repository.name}</span>
      </label>)}</fieldset>)}
      {!integrations.some(item => item.repositories.length) && !error && <p className="muted">No connected repositories.</p>}
    </div> : <label key={field.name}><span>{field.label}</span><input disabled={disabled} value={String(value[field.name] ?? "")} onChange={event => update(field.name, event.target.value)} /></label>)}
    {definition?.webhook && <><p className="muted">Deliveries require a Bearer token for the operator-configured secret and an X-Formicae-Delivery header.</p>{webhookUrl ? <p>Webhook path: <code>{webhookUrl}</code></p> : <p>Save a version to obtain the webhook path.</p>}</>}
  </section>;
}
