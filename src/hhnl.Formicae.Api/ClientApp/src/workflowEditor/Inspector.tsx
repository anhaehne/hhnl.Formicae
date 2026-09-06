import { CustomTaskSettings } from "./CustomTaskSettings";
import { PersonaPicker } from "./PersonaPicker";
import { DecisionSettings } from "./DecisionSettings";
import { StepIcon } from "./StepIcon";
import { useEffect, useState } from "react";
import type { Edge } from "@xyflow/react";
import { getIntegration, listIntegrations, type IntegrationDetail, type WorkflowDefinitionValidationError, type Persona, type PersonaSnapshot, type CustomTaskDefinition, type CustomTaskSnapshot } from "../api";
import { StepModelSettings } from "../StepModelSettings";
import { loopUses, triggerUses, parallelUses, decisionUses, supportedUses, type WorkflowStepNode, type WorkflowStepNodeData } from "../workflowGraph";
import { titleFor } from "./catalog";

type Props = { customTasks: CustomTaskDefinition[]; savedCustomSnapshot?: CustomTaskSnapshot | null; savedPersonaSnapshot?: PersonaSnapshot | null; personas: Persona[]; defaultPersonaId?: string | null; node: WorkflowStepNode; nodes: WorkflowStepNode[]; edges: Edge[]; disabled: boolean; errors: WorkflowDefinitionValidationError[];
  update: (values: Partial<WorkflowStepNodeData>) => void; rename: (id: string) => void; move: (axis: "x" | "y", value: number) => void;
  resizeBranches: (count: number) => void;
  connect: (port: string, target?: string, targetPort?: string) => void; start: () => void; close: () => void; begin: () => void; commit: () => void };
export function Inspector({ customTasks, savedCustomSnapshot, savedPersonaSnapshot, personas, defaultPersonaId, node, nodes, edges, disabled, errors, update, rename, move, connect, resizeBranches, start, close, begin, commit }: Props) {
  const [integrations, setIntegrations] = useState<IntegrationDetail[]>([]);
  const [repositoryError, setRepositoryError] = useState("");
  useEffect(() => { if (node.data.uses !== triggerUses) return; let canceled = false; listIntegrations().then(items => Promise.all(items.map(item => getIntegration(item.id)))).then(items => { if (!canceled) setIntegrations(items); }).catch(() => { if (!canceled) setRepositoryError("Could not load connected repositories. Reopen this inspector to retry."); }); return () => { canceled = true; }; }, []);
  const data = node.data;
  const connection = (port: string, label: string) => {
    const edge = edges.find(edge => edge.source === node.id && edge.sourceHandle === port);
    return <label><span>{label}</span><select aria-label={label} disabled={disabled} value={edge ? JSON.stringify([edge.target, edge.targetHandle || "input"]) : ""} onChange={event => { const value = event.target.value; if (!value) connect(port); else { const [target, targetPort] = JSON.parse(value); connect(port, target, targetPort); } }}>
      <option value="">Not connected</option>
      {nodes.filter(other => other.id !== node.id && other.data.uses !== triggerUses && (port !== "body" || (other.data.uses !== loopUses && other.data.uses !== parallelUses && other.data.uses !== decisionUses)) && (!port.startsWith("branch:") || other.data.uses === "builtins.plan")).flatMap(other => [
        <option key={other.id} value={JSON.stringify([other.id, "input"])}>{other.data.displayName} ({other.id})</option>,
        ...(other.data.uses === loopUses && data.uses !== loopUses && data.uses !== triggerUses && data.uses !== parallelUses && data.uses !== decisionUses ? [<option key={`${other.id}:return`} value={JSON.stringify([other.id, "return"])}>Return to {other.data.displayName} ({other.id})</option>] : [])
        , ...(other.data.uses === parallelUses && data.uses === "builtins.plan" ? [<option key={`${other.id}:join`} value={JSON.stringify([other.id, "join"])}>Join {other.data.displayName} ({other.id})</option>] : [])
      ])}
    </select></label>;
  };
  return <aside className="editor-inspector" aria-label="Step inspector" onFocusCapture={event => { if (event.target.matches("input,select,textarea")) begin(); }} onBlurCapture={event => { if (event.target.matches("input,select,textarea")) commit(); }}>
    <div className="editor-panel-heading"><div className="editor-inspector-identity"><span className={`editor-inspector-icon ${data.loop ? "is-loop" : data.trigger ? "is-trigger" : data.parallel ? "is-parallel" : data.decision ? "is-decision" : ""}`} aria-hidden="true"><StepIcon uses={data.uses} /></span><div><span className="editor-inspector-eyebrow">Step properties</span><h3>{titleFor(data.uses)}</h3></div></div><button type="button" onClick={close} aria-label="Close inspector">×</button></div>
    <div className="editor-inspector-content">
    {errors.map((error, index) => <p role="alert" className="error-text" key={index}>{error.message}</p>)}
    <section className="editor-property-section"><h4>General</h4>
    <label><span>Display Name</span><input disabled={disabled} value={data.displayName} onChange={event => update({ displayName: event.target.value })} /></label>
    {data.uses !== loopUses && data.uses !== triggerUses && data.uses !== parallelUses && data.uses !== decisionUses && <>
      <label><span>Task</span><select aria-label="Task" value={data.uses} disabled={disabled} onChange={event => update({ uses: event.target.value, customTask: event.target.value === "builtins.custom-task" ? { taskId: "", inputs: {} } : undefined, ...(event.target.value === "builtins.create-pull-request" ? { personaId: undefined, personaSnapshot: undefined } : {}) })}>{supportedUses.map(uses => <option key={uses} value={uses}>{titleFor(uses)}</option>)}</select></label>

    </>}
    </section>
    {data.uses !== loopUses && data.uses !== triggerUses && data.uses !== parallelUses && data.uses !== decisionUses && data.uses !== "builtins.create-pull-request" && <section className="editor-property-section"><h4>Model & configuration</h4><StepModelSettings key={node.id} disabled={disabled} aiSettingsId={data.aiSettingsId} model={data.model} onChange={update} /><PersonaPicker label="Step persona" value={data.personaId} inheritedId={defaultPersonaId || "default"} personas={personas} savedSnapshot={savedPersonaSnapshot} disabled={disabled} onChange={personaId => update({ personaId })} /></section>}
    {data.uses === "builtins.custom-task" && <CustomTaskSettings value={data.customTask} tasks={customTasks} savedSnapshot={savedCustomSnapshot} disabled={disabled} onChange={customTask => update({ customTask })} />}
    {data.decision && <DecisionSettings condition={data.decision.condition} nodes={nodes} edges={edges} disabled={disabled} update={condition => update({ decision: { ...data.decision!, condition } })} />}
    {data.parallel && <section className="editor-property-section"><h4>Parallel branches</h4>
      <p className="muted">Run 2–8 independent Plan branches concurrently. Each branch must end at this node’s Join input. Next runs after every branch succeeds. Other task types and nested control nodes are not supported inside branches.</p>
      <label><span>Branch count</span><select aria-label="Branch count" disabled={disabled} value={data.parallel.branchStepIds.length} onChange={event => resizeBranches(Number(event.target.value))}>{Array.from({ length: 7 }, (_, index) => <option key={index + 2} value={index + 2}>{index + 2} branches</option>)}</select></label>
      <p className="muted">Reducing the count disconnects removed branch outputs. Their tasks stay on the canvas.</p>
    </section>}
    {data.loop && <section className="editor-property-section"><h4>Repetition</h4>
      <label><span>Repeat count</span><input type="number" min="1" value={data.loop.repeatCount} disabled={disabled} onChange={event => update({ loop: { ...data.loop!, repeatCount: Number(event.target.value) } })} /></label>
      <label><span>Maximum iterations</span><input type="number" min="1" value={data.loop.maxIterations} disabled={disabled} onChange={event => update({ loop: { ...data.loop!, maxIterations: Number(event.target.value) } })} /></label>
      <label><span>Timeout seconds (optional)</span><input type="number" min="1" value={data.loop.timeoutSeconds ?? ""} disabled={disabled} onChange={event => update({ loop: { ...data.loop!, timeoutSeconds: event.target.value ? Number(event.target.value) : null } })} /></label>
      {data.loop.repeatCount < 1 && <p className="error-text">Repeat count must be at least 1.</p>}
      {data.loop.maxIterations < data.loop.repeatCount && <p className="error-text">Maximum iterations must be at least the repeat count.</p>}
      {data.loop.timeoutSeconds != null && data.loop.timeoutSeconds < 1 && <p className="error-text">Timeout must be positive.</p>}
    </section>}
    {data.trigger && <section className="editor-property-section"><h4>Trigger conditions</h4>
      <p className="editor-event-label">Issue label added</p>
      <label className="toggle-label"><input type="checkbox" checked={data.trigger.enabled} disabled={disabled} onChange={event => update({ trigger: { ...data.trigger!, enabled: event.target.checked } })} /><span>Trigger enabled</span></label>
      <label><span>Label</span><input value={data.trigger.label ?? ""} disabled={disabled} onChange={event => update({ trigger: { ...data.trigger!, label: event.target.value } })} /></label>
      {data.trigger.enabled && !data.trigger.label?.trim() && <p className="error-text">A label is required for an enabled trigger.</p>}
      {repositoryError && <p role="alert">{repositoryError}</p>}
      {integrations.map(integration => <fieldset key={integration.id}><legend>{integration.displayName}</legend>{integration.repositories.map(repository => <label className="toggle-label" key={repository.id}>
        <input type="checkbox" disabled={disabled} checked={data.trigger!.repositoryIds.includes(repository.id)} onChange={event => update({ trigger: { ...data.trigger!, repositoryIds: event.target.checked ? [...data.trigger!.repositoryIds, repository.id] : data.trigger!.repositoryIds.filter(id => id !== repository.id) } })} /><span>{repository.owner}/{repository.name}</span>
      </label>)}</fieldset>)}
      {integrations.every(item => !item.repositories.length) && !repositoryError && <p className="muted">No connected repositories.</p>}
      <label><span>Base Branch</span><input disabled={disabled} placeholder="Repository default" value={data.trigger.baseBranch ?? ""} onChange={event => update({ trigger: { ...data.trigger!, baseBranch: event.target.value } })} /></label>
      <label><span>Workflow model</span><input disabled={disabled} placeholder="Default AI model" value={data.trigger.model ?? ""} onChange={event => update({ trigger: { ...data.trigger!, model: event.target.value } })} /></label>
    </section>}
    <section className="editor-property-section"><h4>Flow connections</h4>
    {data.decision ? <>{connection("true", "True route")}{connection("false", "False route")}</> : data.parallel ? <>{data.parallel.branchStepIds.map((_, index) => <div key={index}>{connection(`branch:${index}`, `Branch ${index + 1}`)}</div>)}{connection("next", "Next step")}</> : data.loop ? <>{connection("body", "Loop body")}{connection("exit", "Loop exit")}<p className="muted">Connect the last body task to Return. Exit runs after all repetitions.</p></> : connection("next", "Next step")}
    <button type="button" disabled={disabled || data.uses === triggerUses} onClick={start}>Set as Start Step</button>
    </section>
    <details className="optional-settings editor-property-advanced"><summary>Advanced</summary>
      <label><span>Step ID</span><input key={node.id} defaultValue={node.id} disabled={disabled} onBlur={event => { rename(event.target.value.trim()); event.target.value = node.id; }} /></label>
      <div className="form-row">{(["x", "y"] as const).map(axis => <label key={axis}><span>Position {axis.toUpperCase()}</span><input type="number" value={Math.round(node.position[axis])} disabled={disabled} onChange={event => move(axis, Number(event.target.value))} /></label>)}</div>
    </details>
    </div>
  </aside>;
}
