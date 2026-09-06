import { StepIcon } from "./StepIcon";
import { createContext, useContext } from "react";
import { Handle, Position, type NodeProps } from "@xyflow/react";
import { loopUses, triggerUses, type WorkflowStepNode } from "../workflowGraph";
import { titleFor } from "./catalog";
export const NodeActions = createContext<{ start: string; errors: Set<string>; editable: boolean; add: (id: string, port: string) => void }>({ start: "", errors: new Set(), editable: false, add: () => {} });
export function WorkflowNode({ id, data, selected }: NodeProps<WorkflowStepNode>) {
  const actions = useContext(NodeActions);
  const loop = data.uses === loopUses, trigger = data.uses === triggerUses;
  const output = (port: string, text: string, top: string) => <div className="editor-port" style={{ top }}><span>{text}</span>{actions.editable && <button className="nodrag nopan" type="button" aria-label={`Add after ${data.displayName} ${text}`} onClick={() => actions.add(id, port)}>+</button>}<Handle id={port} type="source" position={Position.Right} /></div>;
  return <div className={`editor-node ${loop ? "loop" : trigger ? "trigger" : "task"} ${selected ? "selected" : ""} ${actions.errors.has(id) ? "invalid" : ""}`}>
    {!trigger && <Handle id="input" type="target" position={Position.Left} />}
    {loop && <><span className="editor-return">Return</span><Handle id="return" type="target" position={Position.Top} /></>}
    <span className="editor-node-kind"><StepIcon uses={data.uses} /> {titleFor(data.uses)}</span>
    <strong title={data.displayName}>{data.displayName}</strong>
    <span className="editor-node-summary">{loop ? `Repeat ${data.loop?.repeatCount} times` : trigger ? `${data.trigger?.enabled ? "Enabled" : "Disabled"} · ${data.trigger?.label || "Label required"}` : data.uses === "builtins.create-pull-request" ? "Source control action" : data.model || "Inherit workflow model"}</span>
    {actions.start === id && <span className="editor-start">Manual Start</span>}
    {actions.errors.has(id) && <span className="editor-node-error">Needs attention</span>}
    {loop ? <>{output("body", "Body", "48%")} {output("exit", "Exit", "80%")}</> : output("next", "Next", "65%")}
  </div>;
}
