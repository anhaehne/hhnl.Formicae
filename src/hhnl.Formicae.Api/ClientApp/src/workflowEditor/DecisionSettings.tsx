import type { Edge } from "@xyflow/react";
import type { DecisionCondition } from "../api";
import { loopUses, parallelUses, supportedUses, type WorkflowStepNode } from "../workflowGraph";

const operators = [
  ["equals", "Equals"], ["notEquals", "Does not equal"], ["exists", "Exists"],
  ["contains", "Contains"], ["greaterThan", "Greater than"], ["greaterThanOrEqual", "Greater than or equal"],
  ["lessThan", "Less than"], ["lessThanOrEqual", "Less than or equal"]
] as const;
const fields = ["issueUrl", "repositoryUrl", "baseBranch", "model", "planArtifact", "pullRequestUrl"];
const initialScalar = (type: DecisionCondition["valueType"]) => type === "string" ? "" : type === "number" ? 0 : true;
const supports = (operator: string, type: string) => ["equals", "notEquals", "exists"].includes(operator) || (operator === "contains" ? type === "string" : type === "number");

// Group-body task IDs are not stable output identities across concurrent/repeated executions.
function ordinaryTasks(nodes: WorkflowStepNode[], edges: Edge[]) {
  const bodies = new Set<string>();
  for (const group of nodes.filter(node => node.data.uses === loopUses || node.data.uses === parallelUses)) {
    const visit = (id: string) => {
      if (id === group.id || bodies.has(id)) return;
      bodies.add(id);
      for (const edge of edges.filter(edge => edge.source === id && edge.targetHandle !== "return" && edge.targetHandle !== "join")) visit(edge.target);
    };
    edges.filter(edge => edge.source === group.id && (edge.sourceHandle === "body" || edge.sourceHandle?.startsWith("branch:"))).forEach(edge => visit(edge.target));
  }
  return nodes.filter(node => (supportedUses as readonly string[]).includes(node.data.uses) && !bodies.has(node.id));
}

export function DecisionSettings({ condition, nodes, edges, disabled, update }: {
  condition: DecisionCondition; nodes: WorkflowStepNode[]; edges: Edge[]; disabled: boolean; update: (value: DecisionCondition) => void;
}) {
  const scalar = (key: "value" | "compareTo", label: string) => <label><span>{label}</span>{condition.valueType === "boolean"
    ? <select aria-label={label} disabled={disabled} value={condition[key] == null ? "" : String(condition[key])} onChange={event => update({ ...condition, [key]: event.target.value === "true" })}><option value="" disabled>Choose a value</option><option value="true">True</option><option value="false">False</option></select>
    : <input aria-label={label} type={condition.valueType === "number" ? "number" : "text"} step="any" disabled={disabled} value={condition[key] == null ? "" : String(condition[key])} onChange={event => update({ ...condition, [key]: condition.valueType === "number" ? event.target.value === "" ? null : Number(event.target.value) : event.target.value })} />}</label>;
  const candidates = ordinaryTasks(nodes, edges);
  return <section className="editor-property-section"><h4>Decision condition</h4>
    <p className="muted">Only the selected route runs. Decisions are supported outside loop and parallel bodies.</p>
    <label><span>Condition source</span><select aria-label="Condition source" disabled={disabled} value={condition.source} onChange={event => {
      const source = event.target.value as DecisionCondition["source"];
      update({ ...condition, source, reference: source === "workflowField" ? "baseBranch" : source === "taskOutput" ? "" : undefined, value: source === "literal" ? initialScalar(condition.valueType) : undefined });
    }}><option value="literal">Literal value</option><option value="workflowField">Workflow field</option><option value="taskOutput">Task output</option></select></label>
    {condition.source === "workflowField" && <label><span>Workflow field</span><select aria-label="Workflow field" disabled={disabled} value={condition.reference ?? ""} onChange={event => update({ ...condition, reference: event.target.value })}>{fields.map(field => <option key={field} value={field}>{field}</option>)}</select></label>}
    {condition.source === "taskOutput" && <><label><span>Source task</span><select aria-label="Source task" disabled={disabled} value={condition.reference ?? ""} onChange={event => update({ ...condition, reference: event.target.value })}><option value="">Choose a task</option>{condition.reference && !candidates.some(node => node.id === condition.reference) && <option value={condition.reference}>Unavailable: {condition.reference}</option>}{candidates.map(node => <option key={node.id} value={node.id}>{node.data.displayName} ({node.id})</option>)}</select></label><p className="muted">Use the complete output of an ordinary task that runs before this decision on every entry path. Loop and parallel body tasks are excluded. Server validation verifies that the task always precedes the decision.</p></>}
    <label><span>Value type</span><select aria-label="Value type" disabled={disabled} value={condition.valueType} onChange={event => {
      const valueType = event.target.value as DecisionCondition["valueType"];
      update({ ...condition, valueType, operator: supports(condition.operator, valueType) ? condition.operator : "equals", value: condition.source === "literal" ? initialScalar(valueType) : undefined, compareTo: condition.operator === "exists" ? undefined : initialScalar(valueType) });
    }}><option value="string">String</option><option value="number">Number</option><option value="boolean">Boolean</option></select></label>
    {condition.source === "literal" && scalar("value", "Source value")}
    <label><span>Operator</span><select aria-label="Operator" disabled={disabled} value={condition.operator} onChange={event => update({ ...condition, operator: event.target.value as DecisionCondition["operator"], compareTo: event.target.value === "exists" ? undefined : condition.compareTo ?? initialScalar(condition.valueType) })}>{operators.filter(([operator]) => supports(operator, condition.valueType)).map(([operator, title]) => <option key={operator} value={operator}>{title}</option>)}</select></label>
    {condition.operator !== "exists" && scalar("compareTo", "Compare to")}
    <label><span>When source is missing</span><select aria-label="When source is missing" disabled={disabled || condition.operator === "exists"} value={condition.missingValue} onChange={event => update({ ...condition, missingValue: event.target.value as DecisionCondition["missingValue"] })}><option value="error">Fail the workflow</option><option value="false">Take the False route</option></select></label>
    <p className="muted">{condition.operator === "exists" ? "Exists is false only when the source is missing; an empty string exists." : "String comparisons are case-sensitive. Invalid numbers or booleans fail the workflow; values are never guessed."}</p>
  </section>;
}
