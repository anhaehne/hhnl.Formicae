import { inputsFor, outputsFor, issueCommentUses } from "../workflowData";
import { eventDefinition } from "../workflowEvents";
import { StepIcon } from "./StepIcon";
import { createContext, useContext, useEffect, type ReactNode } from "react";
import { Handle, Position, useUpdateNodeInternals, type NodeProps } from "@xyflow/react";
import { endUses, waitUses, loopUses, isStartUses, parallelUses, decisionUses, type WorkflowStepNode } from "../workflowGraph";
import { titleFor } from "./catalog";
export const NodeActions = createContext<{ start: string; errors: Set<string>; editable: boolean; add: (id: string, port: string) => void }>({ start: "", errors: new Set(), editable: false, add: () => {} });
export function WorkflowNode({ id, data, selected, footer }: NodeProps<WorkflowStepNode> & { footer?: ReactNode }) {
  const actions = useContext(NodeActions);
  const updateInternals = useUpdateNodeInternals();
  const branchCount = data.parallel?.branchStepIds.length ?? 2;
  const inputs = inputsFor({ id, data } as WorkflowStepNode), outputs = outputsFor({ id, data } as WorkflowStepNode);
  const schema = inputs.length || outputs.length ? { inputs, outputs } : undefined;
  const schemaKey = JSON.stringify(schema);
  useEffect(() => { updateInternals(id); }, [id, data.uses, data.displayName, branchCount, schemaKey, updateInternals]);
  if (data.variable) return <div className={`editor-node variable ${selected ? "selected" : ""} ${actions.errors.has(id) ? "invalid" : ""}`}>
    <Handle id="data:value" type="target" position={Position.Left} title={`value: ${data.variable.valueType}`} aria-label={`value input: ${data.variable.valueType}`} />
    <span className="editor-node-kind">Variable · {data.variable.valueType}</span>
    <strong title={data.displayName}>{data.displayName}</strong>
    <span className="editor-node-summary">{data.variable.mode === "aggregate" ? data.variable.valueType === "boolean" ? data.variable.booleanOperation === "all" ? "All inputs are true" : "Any input is true" : data.variable.valueType === "number" ? "Sum" : "Append" : data.variable.mode === "first" ? "First value" : "Override"}</span>
    {actions.errors.has(id) && <span className="editor-node-error">Needs attention</span>}
    <Handle id="output:value" type="source" position={Position.Right} title={`value: ${data.variable.valueType}`} aria-label={`value output: ${data.variable.valueType}`} />
  </div>;
  const end = data.uses === endUses;
  const loop = data.uses === loopUses, trigger = isStartUses(data.uses), parallel = data.uses === parallelUses, decision = data.uses === decisionUses;
  const output = (port: string, text: string) => <div key={port} className="editor-port"><span>{text}</span>{actions.editable && <button className="nodrag nopan" type="button" aria-label={`Add after ${data.displayName} ${text}`} onClick={() => actions.add(id, port)}>+</button>}<Handle id={port} type="source" position={Position.Right} /></div>;
  return <div className={`editor-node ${end ? "end" : loop ? "loop" : trigger ? "event" : parallel ? "parallel" : decision ? "decision" : "task"} ${selected ? "selected" : ""} ${actions.errors.has(id) ? "invalid" : ""}`}>
    {!trigger && <Handle id="input" type="target" position={Position.Left} style={{ top: 64 }} />}
    {parallel && <><span className="editor-return">Join</span><Handle id="join" type="target" position={Position.Top} /></>}
    {loop && <><span className="editor-return">Return</span><Handle id="return" type="target" position={Position.Top} /></>}
    <span className="editor-node-kind"><StepIcon uses={data.uses} /> {titleFor(data.uses)}</span>
    <strong title={data.displayName}>{data.displayName}</strong>
    <span className="editor-node-summary">{end ? "Complete workflow · Stop other work" : decision ? `${data.decision?.condition.source === "literal" ? "Literal" : data.decision?.condition.reference || "Source required"} · ${data.decision?.condition.operator || "Condition required"}` : parallel ? `${branchCount} Plan branches · Wait for all` : loop ? `Repeat ${data.loop?.repeatCount} times` : trigger ? `${(data.event?.enabled ?? data.trigger?.enabled) ? "Enabled" : "Disabled"} · ${data.event ? eventDefinition(data.uses)?.manual ? "Manual entrypoint" : data.event.label ? String(data.event.label) : eventDefinition(data.uses)?.description ?? "Event" : data.trigger?.type === "Manual" ? "Manual entrypoint" : data.trigger?.type === "Webhook" ? "Webhook" : data.trigger?.label ?? "Event"}` : ["builtins.create-pull-request", issueCommentUses].includes(data.uses) ? "Source control action" : data.uses === "builtins.script" ? `${data.script?.shell || "sh"} · ${data.script?.timeoutSeconds ?? 300}s · ${data.script?.workingDirectory ?? "workspace"}` : data.uses === "builtins.custom-task" ? `${data.customTask?.snapshot?.name || "Select custom task"} · ${data.model || "Inherited model"}` : data.uses === waitUses ? `Wait for issue ${data.wait?.issueNumber ?? "from input"}` : data.model || "Inherit workflow model"}</span>
    {actions.start === id && <span className="editor-start">Manual Start</span>}
    {actions.errors.has(id) && <span className="editor-node-error">Needs attention</span>}
    <div className="editor-ports">
      <div className="editor-control-ports">
        {end ? null : decision ? <>{output("true", "True")}{output("false", "False")}</> : parallel ? <>{Array.from({ length: branchCount }, (_, index) => output(`branch:${index}`, `Branch ${index + 1}`))}{output("next", "Next")}</> : loop ? <>{output("body", "Body")}{output("exit", "Exit")}</> : output("next", "Next")}
      </div>
      {schema && <div className="editor-data-ports">{Array.from({ length: Math.max(inputs.length, outputs.length) }, (_, index) => <div className="editor-data-row" key={index}>
        <div className="editor-data-input">{inputs[index] && <><Handle id={`data:${inputs[index].name}`} type="target" position={Position.Left} title={`${inputs[index].name}: ${inputs[index].valueType}`} aria-label={`${inputs[index].name} input: ${inputs[index].valueType}`} /><span title={`${inputs[index].name}: ${inputs[index].valueType}`}>{inputs[index].name === "issueId" ? "Issue id" : inputs[index].name === "text" && data.uses === issueCommentUses ? "Text" : inputs[index].name}</span></>}</div>
        <div className="editor-data-output">{outputs[index] && <><span title={`${outputs[index].name}: ${outputs[index].valueType}`}>{outputs[index].name === "issueId" ? "Issue id" : outputs[index].name}</span><Handle id={`output:${outputs[index].name}`} type="source" position={Position.Right} title={`${outputs[index].name}: ${outputs[index].valueType}`} aria-label={`${outputs[index].name} output: ${outputs[index].valueType}`} /></>}</div>
      </div>)}</div>}
    </div>
    {footer}
  </div>;
}
