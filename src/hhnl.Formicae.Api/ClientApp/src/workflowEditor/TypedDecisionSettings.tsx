import { ScalarField } from "./CustomTaskSchema";
import type { Edge } from "@xyflow/react";
import type { CustomTaskDefinition, WorkflowDecisionNodeSettings, WorkflowDecisionCase } from "../api";
import { eligibleProducer, type WorkflowStepNode } from "../workflowGraph";
import { outputsFor } from "../workflowData";

const operators = [["equals", "="], ["notEquals", "!="], ["greaterThan", ">"], ["greaterThanOrEqual", ">="], ["lessThan", "<"], ["lessThanOrEqual", "<="]] as const;
export function TypedDecisionSettings({ value, nodes, edges, tasks, stepId, start, disabled, update, connect, disconnect }: {
  value: WorkflowDecisionNodeSettings; nodes: WorkflowStepNode[]; edges: Edge[]; tasks: CustomTaskDefinition[];
  stepId: string; start: string; disabled: boolean; update: (value: WorkflowDecisionNodeSettings) => void;
  disconnect: (edgeId: string) => void;
  connect: (source: string, port: string, target?: string, targetPort?: string) => void;
}) {
  const binding = edges.find(edge => edge.target === stepId && edge.targetHandle === "data:value");
  const candidates = nodes.filter(node => eligibleProducer(nodes, edges, start, node.id, stepId)).flatMap(node => outputsFor(node, tasks).map(output => ({ key: JSON.stringify([node.id, output.name]), label: `${node.data.displayName} · ${output.name} (${output.valueType})` })));
  const selected = binding ? JSON.stringify([binding.source, binding.sourceHandle!.slice(7)]) : "";
  const editCase = (id: string, patch: Partial<WorkflowDecisionCase>) => update({ ...value, cases: value.cases?.map(item => item.id === id ? { ...item, ...patch } : item) });
  return <section className="editor-property-section"><h4>Decision input</h4>
    <label><span>Input source</span><select aria-label="Decision input source" disabled={disabled} value={selected} onChange={event => {
      if (event.target.value) { const [source, output] = JSON.parse(event.target.value); connect(source, `output:${output}`, stepId, "data:value"); }
      else if (binding) disconnect(binding.id);
    }}><option value="">Connect an input</option>{selected && !candidates.some(item => item.key === selected) && <option value={selected}>Unavailable input</option>}{candidates.map(item => <option key={item.key} value={item.key}>{item.label}</option>)}</select></label>
    <p className="muted">Input type: {value.inputType}. Connect a typed output or variable. Only the selected exit runs.</p>
    {value.inputType === "boolean" && <p>True takes the True exit; false takes the False exit.</p>}
    {(value.inputType === "string" || value.inputType === "number") && <>
      <h4>{value.inputType === "string" ? "String values" : "Numeric comparisons"}</h4>
      {(value.cases ?? []).map((item, index) => <fieldset key={item.id}><legend>Case {index + 1}</legend>
        {value.inputType === "number" && <label><span>Comparison</span><select aria-label={`Comparison for case ${index + 1}`} disabled={disabled} value={item.operator} onChange={event => editCase(item.id, { operator: event.target.value as WorkflowDecisionCase["operator"] })}>{operators.map(([operator, label]) => <option key={operator} value={operator}>{label}</option>)}</select></label>}
        <ScalarField label={`Value for case ${index + 1}`} type={value.inputType!} disabled={disabled} value={item.value} onChange={input => editCase(item.id, { value: input as string | number })} />
        <button type="button" disabled={disabled} onClick={() => update({ ...value, cases: value.cases?.filter(other => other.id !== item.id) })}>Remove case {index + 1}</button>
        {index > 0 && <button type="button" disabled={disabled} onClick={() => { const cases = [...value.cases!]; [cases[index - 1], cases[index]] = [cases[index], cases[index - 1]]; update({ ...value, cases }); }}>Move case {index + 1} up</button>}
      </fieldset>)}
      <button type="button" disabled={disabled || (value.cases?.length ?? 0) >= 32} onClick={() => update({ ...value, cases: [...value.cases ?? [], { id: crypto.randomUUID(), operator: "equals", value: value.inputType === "number" ? 0 : "", stepId: "" }] })}>Add case</button>
      <p className="muted">{value.inputType === "string" ? "Values match exactly and are case-sensitive." : "Comparisons are checked in the displayed order; the first match wins."} Connect the mandatory Default exit for unmatched inputs.</p>
    </>}
  </section>;
}
