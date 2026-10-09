import { useState } from "react";
import type { Edge } from "@xyflow/react";
import type { CustomTaskDefinition, WorkflowDataVariable, WorkflowDefinitionValidationError } from "../api";
import { outputsFor } from "../workflowData";
import { validVariableSource, type WorkflowStepNode, type WorkflowStepNodeData } from "../workflowGraph";

export function VariableSettings({ node, nodes, edges, start, tasks, disabled, errors, update, rename, move, close, begin, commit }: {
  node: WorkflowStepNode; nodes: WorkflowStepNode[]; edges: Edge[]; start: string; tasks: CustomTaskDefinition[];
  disabled: boolean; errors: WorkflowDefinitionValidationError[]; update: (values: Partial<WorkflowStepNodeData>) => void;
  rename: (id: string) => void; move: (axis: "x" | "y", value: number) => void; close: () => void; begin: () => void; commit: () => void;
}) {
  const variable = node.data.variable!;
  const [selected, setSelected] = useState("");
  const change = (values: Partial<WorkflowDataVariable>) => update({ variable: { ...variable, ...values } });
  const candidates = nodes.filter(other => validVariableSource(nodes, edges, start, other.id, node.id)).flatMap(other =>
    outputsFor(other, tasks).filter(output => output.valueType === variable.valueType && !variable.sources.some(source => source.stepId === other.id && source.outputName === output.name))
      .map(output => ({ value: JSON.stringify([other.id, output.name]), label: `${other.data.displayName} · ${output.name}` })));
  const reorder = (index: number, direction: number) => {
    const sources = [...variable.sources];
    [sources[index], sources[index + direction]] = [sources[index + direction], sources[index]];
    change({ sources });
  };
  return <aside className="editor-inspector" aria-label="Variable inspector" onFocusCapture={event => { if (event.target.matches("input,select,textarea")) begin(); }} onBlurCapture={commit}>
    <div className="editor-panel-heading"><h3>Variable</h3><button type="button" onClick={close} aria-label="Close inspector">×</button></div>
    <div className="editor-inspector-content">
      {errors.map((error, index) => <p role="alert" className="error-text" key={index}>{error.message}</p>)}
      <section className="editor-property-section"><h4>General</h4>
        <label><span>Name</span><input aria-label="Variable name" value={node.data.displayName} disabled={disabled} maxLength={120} onChange={event => update({ displayName: event.target.value })} /></label>
        <label><span>Type</span><select aria-label="Variable type" value={variable.valueType} disabled={disabled} onChange={event => change({ valueType: event.target.value as WorkflowDataVariable["valueType"] })}>
          <option value="string">String</option><option value="number">Number</option><option value="boolean">Boolean</option>
        </select></label>
        <label><span>Combination</span><select aria-label="Variable combination" disabled={disabled} value={variable.mode} onChange={event => change({ mode: event.target.value as WorkflowDataVariable["mode"] })}>
          <option value="aggregate">Aggregate</option><option value="first">First</option><option value="override">Override</option>
        </select></label>
        {variable.mode === "aggregate" && variable.valueType === "string" && <label><span>Separator</span><textarea aria-label="Variable separator" rows={2} disabled={disabled} value={variable.separator} onChange={event => change({ separator: event.target.value })} /><small>Defaults to a newline. Leave empty to append directly.</small></label>}
        {variable.mode === "aggregate" && variable.valueType === "number" && <p>Sum all available input values.</p>}
        {variable.mode === "aggregate" && variable.valueType === "boolean" && <label><span>Boolean operation</span><select aria-label="Boolean operation" disabled={disabled} value={variable.booleanOperation} onChange={event => change({ booleanOperation: event.target.value as "any" | "all" })}>
          <option value="any">Any input is true (OR)</option><option value="all">All inputs are true (AND)</option>
        </select></label>}
      </section>
      <section className="editor-property-section"><h4>Ordered inputs</h4>
        <p className="muted">{variable.mode === "first" ? "Use the first available value in this list." : variable.mode === "override" ? "Use the last available value in this list." : "Combine available values in this order."} Missing outputs are skipped. False, zero and empty text are values.</p>
        <ol className="variable-sources">{variable.sources.map((source, index) => <li key={`${source.stepId}:${source.outputName}`}>
          <span>{nodes.find(other => other.id === source.stepId)?.data.displayName ?? source.stepId} · {source.outputName}</span>
          <div><button type="button" aria-label={`Move source ${index + 1} up`} disabled={disabled || index === 0} onClick={() => reorder(index, -1)}>↑</button>
            <button type="button" aria-label={`Move source ${index + 1} down`} disabled={disabled || index === variable.sources.length - 1} onClick={() => reorder(index, 1)}>↓</button>
            <button type="button" aria-label={`Remove source ${index + 1}`} disabled={disabled} onClick={() => change({ sources: variable.sources.filter((_, offset) => offset !== index) })}>Remove</button></div>
        </li>)}</ol>
        <label><span>Add source</span><select aria-label="Variable source" disabled={disabled} value={candidates.some(candidate => candidate.value === selected) ? selected : ""} onChange={event => setSelected(event.target.value)}>
          <option value="">Choose a matching output</option>{candidates.map(candidate => <option key={candidate.value} value={candidate.value}>{candidate.label}</option>)}
        </select></label>
        <button type="button" disabled={disabled || !candidates.some(candidate => candidate.value === selected)} onClick={() => { const [stepId, outputName] = JSON.parse(selected); change({ sources: [...variable.sources, { stepId, outputName }] }); setSelected(""); }}>Add source</button>
        <p className="muted">Connect the variable's output to a matching task input. Control connections must place every producer before that task.</p>
      </section>
      <details className="optional-settings"><summary>Advanced</summary><label><span>Variable ID</span><input key={node.id} defaultValue={node.id} disabled={disabled} onBlur={event => rename(event.target.value.trim())} /></label>
        {(["x", "y"] as const).map(axis => <label key={axis}><span>Position {axis.toUpperCase()}</span><input type="number" value={Math.round(node.position[axis])} disabled={disabled} onChange={event => move(axis, Number(event.target.value))} /></label>)}
      </details>
    </div>
  </aside>;
}
