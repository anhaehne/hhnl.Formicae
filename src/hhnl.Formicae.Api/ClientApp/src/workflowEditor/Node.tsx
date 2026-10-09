import { inputsFor, outputsFor, issueCommentUses } from "../workflowData";
import { eventDefinition } from "../workflowEvents";
import { StepIcon } from "./StepIcon";
import { createContext, useContext, useEffect } from "react";
import { Handle, Position, useUpdateNodeInternals, type NodeProps } from "@xyflow/react";
import { waitUses, loopUses, isStartUses, parallelUses, decisionUses, type WorkflowStepNode } from "../workflowGraph";
import { titleFor } from "./catalog";
export const NodeActions = createContext<{ start: string; errors: Set<string>; editable: boolean; add: (id: string, port: string) => void }>({ start: "", errors: new Set(), editable: false, add: () => {} });
export function WorkflowNode({ id, data, selected }: NodeProps<WorkflowStepNode>) {
  const actions = useContext(NodeActions);
  const updateInternals = useUpdateNodeInternals();
  const branchCount = data.parallel?.branchStepIds.length ?? 2;
  const inputs = inputsFor({ id, data } as WorkflowStepNode), outputs = outputsFor({ id, data } as WorkflowStepNode);
  const schema = inputs.length || outputs.length ? { inputs, outputs } : undefined;
  const schemaKey = JSON.stringify(schema);
  useEffect(() => { if (data.uses === parallelUses || data.uses === "builtins.agent-task" || data.uses === "builtins.custom-task" || data.uses === "builtins.script" || data.uses === waitUses || data.uses === issueCommentUses || data.uses === "github.issue-created") updateInternals(id); }, [id, data.uses, branchCount, schemaKey, updateInternals]);
  const loop = data.uses === loopUses, trigger = isStartUses(data.uses), parallel = data.uses === parallelUses, decision = data.uses === decisionUses;
  const output = (port: string, text: string, top: string) => <div key={port} className="editor-port" style={{ top }}><span>{text}</span>{actions.editable && <button className="nodrag nopan" type="button" aria-label={`Add after ${data.displayName} ${text}`} onClick={() => actions.add(id, port)}>+</button>}<Handle id={port} type="source" position={Position.Right} /></div>;
  return <div className={`editor-node ${loop ? "loop" : trigger ? "event" : parallel ? "parallel" : decision ? "decision" : "task"} ${selected ? "selected" : ""} ${actions.errors.has(id) ? "invalid" : ""}`} style={schema ? { minHeight: 145 + Math.max(schema.inputs.length, schema.outputs?.length ?? 0) * 28 } : data.uses === "builtins.script" ? { minHeight: 175 } : parallel ? { minHeight: 142 + branchCount * 34 } : decision ? { minHeight: 160 } : undefined}>
    {!trigger && <Handle id="input" type="target" position={Position.Left} style={data.uses === waitUses ? { top: 108 } : undefined} />}
    {parallel && <><span className="editor-return">Join</span><Handle id="join" type="target" position={Position.Top} /></>}
    {loop && <><span className="editor-return">Return</span><Handle id="return" type="target" position={Position.Top} /></>}
    <span className="editor-node-kind"><StepIcon uses={data.uses} /> {titleFor(data.uses)}</span>
    <strong title={data.displayName}>{data.displayName}</strong>
    <span className="editor-node-summary">{decision ? `${data.decision?.condition.source === "literal" ? "Literal" : data.decision?.condition.reference || "Source required"} · ${data.decision?.condition.operator || "Condition required"}` : parallel ? `${branchCount} Plan branches · Wait for all` : loop ? `Repeat ${data.loop?.repeatCount} times` : trigger ? `${(data.event?.enabled ?? data.trigger?.enabled) ? "Enabled" : "Disabled"} · ${data.event ? eventDefinition(data.uses)?.manual ? "Manual entrypoint" : data.event.label ? String(data.event.label) : eventDefinition(data.uses)?.description ?? "Event" : data.trigger?.type === "Manual" ? "Manual entrypoint" : data.trigger?.type === "Webhook" ? "Webhook" : data.trigger?.label ?? "Event"}` : ["builtins.create-pull-request", issueCommentUses].includes(data.uses) ? "Source control action" : data.uses === "builtins.script" ? `${data.script?.shell || "sh"} · ${data.script?.timeoutSeconds ?? 300}s · ${data.script?.workingDirectory ?? "workspace"}` : data.uses === "builtins.custom-task" ? `${data.customTask?.snapshot?.name || "Select custom task"} · ${data.model || "Inherited model"}` : data.uses === waitUses ? `Wait for issue ${data.wait?.issueNumber ?? "from input"}` : data.model || "Inherit workflow model"}</span>
    {actions.start === id && <span className="editor-start">Manual Start</span>}
    {actions.errors.has(id) && <span className="editor-node-error">Needs attention</span>}
    {schema?.inputs.map((input, index) => <div key={`input:${input.name}`} className="editor-data-input" style={{ position: "absolute", left: 0, top: 140 + index * 28, fontSize: 11 }}><Handle id={`data:${input.name}`} type="target" position={Position.Left} style={{ background: "#168b85" }} /><span style={{ marginLeft: 10 }}>{input.name === "issueId" ? "Issue id" : input.name === "text" && data.uses === issueCommentUses ? "Text" : input.name}</span></div>)}
    {(schema?.outputs ?? []).map((output, index) => <div key={`output:${output.name}`} className="editor-data-output" style={{ position: "absolute", right: 0, top: 140 + index * 28, fontSize: 11 }}><span style={{ marginRight: 10 }}>{output.name === "issueId" ? "Issue id" : output.name}</span><Handle id={`output:${output.name}`} type="source" position={Position.Right} style={{ background: "#168b85" }} /></div>)}
    {decision ? <>{output("true", "True", "58%")} {output("false", "False", "82%")}</> : parallel ? <>{Array.from({ length: branchCount }, (_, index) => output(`branch:${index}`, `Branch ${index + 1}`, `${112 + index * 34}px`))}{output("next", "Next", `${116 + branchCount * 34}px`)}</> : loop ? <>{output("body", "Body", "48%")} {output("exit", "Exit", "80%")}</> : output("next", "Next", data.uses === waitUses ? "108px" : "65%")}
  </div>;
}
