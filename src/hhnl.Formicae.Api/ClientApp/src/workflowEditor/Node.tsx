import { StepIcon } from "./StepIcon";
import { createContext, useContext, useEffect } from "react";
import { Handle, Position, useUpdateNodeInternals, type NodeProps } from "@xyflow/react";
import { loopUses, triggerUses, parallelUses, decisionUses, type WorkflowStepNode } from "../workflowGraph";
import { titleFor } from "./catalog";
export const NodeActions = createContext<{ start: string; errors: Set<string>; editable: boolean; add: (id: string, port: string) => void }>({ start: "", errors: new Set(), editable: false, add: () => {} });
export function WorkflowNode({ id, data, selected }: NodeProps<WorkflowStepNode>) {
  const actions = useContext(NodeActions);
  const updateInternals = useUpdateNodeInternals();
  const branchCount = data.parallel?.branchStepIds.length ?? 2;
  useEffect(() => { if (data.uses === parallelUses) updateInternals(id); }, [id, data.uses, branchCount, updateInternals]);
  const loop = data.uses === loopUses, trigger = data.uses === triggerUses, parallel = data.uses === parallelUses, decision = data.uses === decisionUses;
  const output = (port: string, text: string, top: string) => <div key={port} className="editor-port" style={{ top }}><span>{text}</span>{actions.editable && <button className="nodrag nopan" type="button" aria-label={`Add after ${data.displayName} ${text}`} onClick={() => actions.add(id, port)}>+</button>}<Handle id={port} type="source" position={Position.Right} /></div>;
  return <div className={`editor-node ${loop ? "loop" : trigger ? "trigger" : parallel ? "parallel" : decision ? "decision" : "task"} ${selected ? "selected" : ""} ${actions.errors.has(id) ? "invalid" : ""}`} style={parallel ? { minHeight: 142 + branchCount * 34 } : decision ? { minHeight: 160 } : undefined}>
    {!trigger && <Handle id="input" type="target" position={Position.Left} />}
    {parallel && <><span className="editor-return">Join</span><Handle id="join" type="target" position={Position.Top} /></>}
    {loop && <><span className="editor-return">Return</span><Handle id="return" type="target" position={Position.Top} /></>}
    <span className="editor-node-kind"><StepIcon uses={data.uses} /> {titleFor(data.uses)}</span>
    <strong title={data.displayName}>{data.displayName}</strong>
    <span className="editor-node-summary">{decision ? `${data.decision?.condition.source === "literal" ? "Literal" : data.decision?.condition.reference || "Source required"} · ${data.decision?.condition.operator || "Condition required"}` : parallel ? `${branchCount} Plan branches · Wait for all` : loop ? `Repeat ${data.loop?.repeatCount} times` : trigger ? `${data.trigger?.enabled ? "Enabled" : "Disabled"} · ${data.trigger?.label || "Label required"}` : data.uses === "builtins.create-pull-request" ? "Source control action" : data.model || "Inherit workflow model"}</span>
    {actions.start === id && <span className="editor-start">Manual Start</span>}
    {actions.errors.has(id) && <span className="editor-node-error">Needs attention</span>}
    {decision ? <>{output("true", "True", "58%")} {output("false", "False", "82%")}</> : parallel ? <>{Array.from({ length: branchCount }, (_, index) => output(`branch:${index}`, `Branch ${index + 1}`, `${112 + index * 34}px`))}{output("next", "Next", `${116 + branchCount * 34}px`)}</> : loop ? <>{output("body", "Body", "48%")} {output("exit", "Exit", "80%")}</> : output("next", "Next", "65%")}
  </div>;
}
