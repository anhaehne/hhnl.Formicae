import { issueCommentUses } from "../workflowData";
import { IssueCommentSettings } from "./IssueCommentSettings";
import { WaitSettings } from "./WaitSettings";
import { ImagePicker } from "./ImagePicker";
import { EventSettings } from "./EventSettings";
import { ScriptSettings, ExecutionSettings, defaultScript } from "./ExecutionSettings";
import { EnvironmentPicker } from "./EnvironmentPicker";
import { AgentTaskSettings } from "./AgentTaskSettings";
import { CustomTaskSettings } from "./CustomTaskSettings";
import { PersonaPicker } from "./PersonaPicker";
import { DecisionSettings } from "./DecisionSettings";
import { StepIcon } from "./StepIcon";
import type { Edge } from "@xyflow/react";
import { type WorkflowDefinitionValidationError, type Persona, type PersonaSnapshot, type CustomTaskDefinition, type CustomTaskSnapshot, type EnvironmentProfile, type EnvironmentSnapshot } from "../api";
import { StepModelSettings } from "../StepModelSettings";
import { waitUses, scriptUses, loopUses, isStartUses, parallelUses, decisionUses, supportedUses, type WorkflowStepNode, type WorkflowStepNodeData } from "../workflowGraph";
import { titleFor } from "./catalog";

type Props = { webhookUrl?: string; workflowStart: string; environments: EnvironmentProfile[]; defaultEnvironmentId?: string | null; savedEnvironmentSnapshot?: EnvironmentSnapshot | null; customTasks: CustomTaskDefinition[]; savedCustomSnapshot?: CustomTaskSnapshot | null; savedPersonaSnapshot?: PersonaSnapshot | null; personas: Persona[]; defaultPersonaId?: string | null; node: WorkflowStepNode; nodes: WorkflowStepNode[]; edges: Edge[]; disabled: boolean; errors: WorkflowDefinitionValidationError[];
  update: (values: Partial<WorkflowStepNodeData>) => void; rename: (id: string) => void; move: (axis: "x" | "y", value: number) => void;
  resizeBranches: (count: number) => void;
  connect: (port: string, target?: string, targetPort?: string) => void; disconnect: (edgeId: string) => void; close: () => void; begin: () => void; commit: () => void };
export function Inspector({ webhookUrl, workflowStart, environments, defaultEnvironmentId, savedEnvironmentSnapshot, customTasks, savedCustomSnapshot, savedPersonaSnapshot, personas, defaultPersonaId, node, nodes, edges, disabled, errors, update, rename, move, connect, disconnect, resizeBranches, close, begin, commit }: Props) {
  const data = node.data;
  const workerTask = ![waitUses, loopUses, parallelUses, decisionUses, "builtins.create-pull-request", issueCommentUses].includes(data.uses) && !isStartUses(data.uses);
  const profileId = data.environmentId ?? defaultEnvironmentId ?? "default";
  const profile = environments.find(item => item.id === profileId) ?? (savedEnvironmentSnapshot?.id === profileId ? savedEnvironmentSnapshot : undefined);
  const connection = (port: string, label: string) => {
    const edge = edges.find(edge => edge.source === node.id && edge.sourceHandle === port);
    return <label><span>{label}</span><select aria-label={label} disabled={disabled} value={edge ? JSON.stringify([edge.target, edge.targetHandle || "input"]) : ""} onChange={event => { const value = event.target.value; if (!value) connect(port); else { const [target, targetPort] = JSON.parse(value); connect(port, target, targetPort); } }}>
      <option value="">Not connected</option>
      {nodes.filter(other => other.id !== node.id && !isStartUses(other.data.uses) && (port !== "body" || (other.data.uses !== loopUses && other.data.uses !== parallelUses && other.data.uses !== decisionUses)) && (!port.startsWith("branch:") || other.data.uses === "builtins.plan")).flatMap(other => [
        <option key={other.id} value={JSON.stringify([other.id, "input"])}>{other.data.displayName} ({other.id})</option>,
        ...(other.data.uses === loopUses && data.uses !== loopUses && !isStartUses(data.uses) && data.uses !== parallelUses && data.uses !== decisionUses ? [<option key={`${other.id}:return`} value={JSON.stringify([other.id, "return"])}>Return to {other.data.displayName} ({other.id})</option>] : [])
        , ...(other.data.uses === parallelUses && data.uses === "builtins.plan" ? [<option key={`${other.id}:join`} value={JSON.stringify([other.id, "join"])}>Join {other.data.displayName} ({other.id})</option>] : [])
      ])}
    </select></label>;
  };
  return <aside className="editor-inspector" aria-label="Step inspector" onFocusCapture={event => { if (event.target.matches("input,select,textarea")) begin(); }} onBlurCapture={event => { if (event.target.matches("input,select,textarea")) commit(); }}>
    <div className="editor-panel-heading"><div className="editor-inspector-identity"><span className={`editor-inspector-icon ${data.loop ? "is-loop" : data.event ? "is-event" : data.parallel ? "is-parallel" : data.decision ? "is-decision" : ""}`} aria-hidden="true"><StepIcon uses={data.uses} /></span><div><span className="editor-inspector-eyebrow">Step properties</span><h3>{titleFor(data.uses)}</h3></div></div><button type="button" onClick={close} aria-label="Close inspector">×</button></div>
    <div className="editor-inspector-content">
    {errors.map((error, index) => <p role="alert" className="error-text" key={index}>{error.message}</p>)}
    <section className="editor-property-section"><h4>General</h4>
    <label><span>Display Name</span><input disabled={disabled} value={data.displayName} onChange={event => update({ displayName: event.target.value })} /></label>
    {data.uses !== loopUses && !isStartUses(data.uses) && data.uses !== parallelUses && data.uses !== decisionUses && <>
      <label><span>Task</span><select aria-label="Task" value={data.uses} disabled={disabled} onChange={event => update({ uses: event.target.value, wait: event.target.value === waitUses ? { issueNumber: 1 } : undefined, issueComment: event.target.value === issueCommentUses ? { inputs: {} } : undefined, customTask: event.target.value === "builtins.agent-task" ? { taskId: "", inputs: {}, definition: { promptTemplate: "", inputs: [], outputs: [], runner: { kind: "agent", timeoutSeconds: 1800 } } } : event.target.value === "builtins.custom-task" ? { taskId: "", inputs: {} } : undefined, script: event.target.value === scriptUses ? defaultScript : undefined, ...(event.target.value === scriptUses ? { aiSettingsId: undefined, model: undefined, personaId: undefined, personaSnapshot: undefined, capabilities: null } : {}), ...([waitUses, "builtins.create-pull-request", issueCommentUses].includes(event.target.value) ? { aiSettingsId: undefined, model: undefined, personaId: undefined, personaSnapshot: undefined, environmentId: undefined, environmentSnapshot: undefined, imageSelection: undefined, imageSnapshot: undefined, capabilities: undefined, secretReferences: undefined } : {}) })}>{supportedUses.map(uses => <option key={uses} value={uses}>{titleFor(uses)}</option>)}</select></label>

    </>}
    </section>
    {data.uses !== loopUses && !isStartUses(data.uses) && data.uses !== parallelUses && data.uses !== decisionUses && data.uses !== "builtins.create-pull-request" && data.uses !== issueCommentUses && data.uses !== waitUses && <section className="editor-property-section"><h4>{data.uses === scriptUses ? "Environment" : "Model & configuration"}</h4>{data.uses !== scriptUses && <StepModelSettings key={node.id} disabled={disabled} aiSettingsId={data.aiSettingsId} model={data.model} onChange={update} />}<EnvironmentPicker label="Step environment" inheritedId={defaultEnvironmentId ?? "default"} value={data.environmentId} environments={environments} savedSnapshot={savedEnvironmentSnapshot} disabled={disabled} onChange={environmentId => update({ environmentId })} />{data.uses !== scriptUses && <PersonaPicker label="Step persona" value={data.personaId} inheritedId={defaultPersonaId || "default"} personas={personas} savedSnapshot={savedPersonaSnapshot} disabled={disabled} onChange={personaId => update({ personaId })} />}</section>}
    {data.uses === issueCommentUses && <IssueCommentSettings nodes={nodes} edges={edges} stepId={node.id} start={workflowStart} value={data.issueComment} tasks={customTasks} disabled={disabled} onChange={issueComment => update({ issueComment })} />}
    {data.uses === waitUses && <WaitSettings value={data.wait} nodes={nodes} edges={edges} stepId={node.id} start={workflowStart} tasks={customTasks} disabled={disabled} onChange={wait => update({ wait })} />}
    {data.uses === scriptUses && <ScriptSettings value={data.script} disabled={disabled} onChange={script => update({ script })} />}
    {workerTask && <ImagePicker value={data.imageSelection} snapshot={data.imageSnapshot} disabled={disabled} onChange={imageSelection => update({ imageSelection, imageSnapshot: undefined })} />}
    {workerTask && <ExecutionSettings capabilities={data.capabilities} references={data.secretReferences} environment={profile} script={data.uses === scriptUses} disabled={disabled} onChange={update} />}
    {data.uses === "builtins.agent-task" && <AgentTaskSettings nodes={nodes} edges={edges} stepId={node.id} start={workflowStart} value={data.customTask} tasks={customTasks} disabled={disabled} onChange={customTask => update({ customTask })} />}
    {data.uses === "builtins.custom-task" && <CustomTaskSettings nodes={nodes} edges={edges} stepId={node.id} start={workflowStart} value={data.customTask} tasks={customTasks} savedSnapshot={savedCustomSnapshot} disabled={disabled} onChange={customTask => update({ customTask })} />}
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
    {data.event && <EventSettings uses={data.uses} value={data.event} disabled={disabled} webhookUrl={webhookUrl} onChange={event => update({ event })} />}
    <section className="editor-property-section"><h4>Flow connections</h4>
    {data.decision ? <>{connection("true", "True route")}{connection("false", "False route")}</> : data.parallel ? <>{data.parallel.branchStepIds.map((_, index) => <div key={index}>{connection(`branch:${index}`, `Branch ${index + 1}`)}</div>)}{connection("next", "Next step")}</> : data.loop ? <>{connection("body", "Loop body")}{connection("exit", "Loop exit")}<p className="muted">Connect the last body task to Return. Exit runs after all repetitions.</p></> : connection("next", "Next step")}
    {!data.decision && !data.parallel && !data.loop && !data.event && <>
      <p className="muted">Connect multiple next steps to run them in parallel. Each step waits for all incoming tasks to succeed.</p>
      {edges.filter(edge => edge.source === node.id && edge.sourceHandle === "next").map(edge => <div key={edge.id}>
        <span>{nodes.find(other => other.id === edge.target)?.data.displayName ?? edge.target}</span>
        <button type="button" disabled={disabled} aria-label={`Disconnect ${edge.target}`} onClick={() => disconnect(edge.id)}>Disconnect</button>
      </div>)}
    </>}
    </section>
    <details className="optional-settings editor-property-advanced"><summary>Advanced</summary>
      <label><span>Step ID</span><input key={node.id} defaultValue={node.id} disabled={disabled} onBlur={event => { rename(event.target.value.trim()); event.target.value = node.id; }} /></label>
      <div className="form-row">{(["x", "y"] as const).map(axis => <label key={axis}><span>Position {axis.toUpperCase()}</span><input type="number" value={Math.round(node.position[axis])} disabled={disabled} onChange={event => move(axis, Number(event.target.value))} /></label>)}</div>
    </details>
    </div>
  </aside>;
}
