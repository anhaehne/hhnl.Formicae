import { waitUses, waitOutputs, eligibleProducer, type WorkflowStepNode } from "../workflowGraph";
import type { Edge } from "@xyflow/react";
import type { CustomTaskDefinition, CustomTaskSnapshot, WorkflowCustomTaskSettings } from "../api";
import { initialScalar, ScalarField } from "./CustomTaskSchema";

export function CustomTaskSettings({ value, tasks, savedSnapshot, inline = false, nodes, edges, stepId, start, disabled, onChange }: { inline?: boolean; nodes: WorkflowStepNode[]; edges: Edge[]; stepId: string; start: string; value?: WorkflowCustomTaskSettings | null; tasks: CustomTaskDefinition[]; savedSnapshot?: CustomTaskSnapshot | null; disabled: boolean; onChange: (value: WorkflowCustomTaskSettings) => void }) {
  const current = inline ? value?.snapshot : tasks.find(task => task.id === value?.taskId), saved = savedSnapshot?.id === value?.taskId ? savedSnapshot : undefined;
  const preview = current || saved, values = value?.inputs || {};
  return <section className="editor-property-section"><h4>{inline ? "Input values" : "Custom task"}</h4>
    {!inline && <label><span>Task definition</span><select aria-label="Task definition" disabled={disabled} value={value?.taskId || ""} onChange={event => onChange({ taskId: event.target.value, inputs: {}, snapshot: tasks.find(task => task.id === event.target.value) })}><option value="">Choose a custom task</option>{value?.taskId && !current && <option value={value.taskId}>Unavailable: {saved?.name || value.taskId}</option>}{tasks.map(task => <option value={task.id} key={task.id}>{task.name} · revision {task.revision}</option>)}</select></label>}
    {saved && <p className="muted">Saved task: {saved.name} · revision {saved.revision}</p>}
    {saved && current && saved.revision !== current.revision && <p className="persona-revision-notice">Saved version uses task revision {saved.revision}; Save Version will use revision {current.revision}.</p>}
    {!current && value?.taskId && <p className="persona-revision-notice">{saved ? "The saved version remains runnable with its recorded task. " : "This custom task is unavailable. "}A new enabled version needs an active task selection.</p>}
    {preview && <><p className="muted">{preview.description}</p><p className="muted">Agent runner · {preview.runner.timeoutSeconds}s timeout · scratch workspace</p>
      {preview.inputs.map(input => { const present = Object.hasOwn(values, input.name); const binding = value?.bindings?.[input.name];
        const candidates = nodes.filter(node => [waitUses, "builtins.agent-task", "builtins.custom-task", "builtins.script"].includes(node.data.uses) && eligibleProducer(nodes, edges, start, node.id, stepId)).flatMap(node => {
          const schema = node.data.customTask?.definition ?? tasks.find(task => task.id === node.data.customTask?.taskId) ?? node.data.customTask?.snapshot;
          return (node.data.uses === waitUses ? waitOutputs : node.data.uses === "builtins.script" ? [{ name: "output", valueType: "string" }] : schema?.outputs ?? []).filter(output => output.valueType === input.valueType).map(output => ({ value: JSON.stringify([node.id, output.name]), label: `${node.data.displayName} · ${output.name}`, stepId: node.id, outputName: output.name }));
        });
        const selected = binding ? JSON.stringify([binding.stepId, binding.outputName]) : "";
        return <fieldset key={input.name}><legend>{input.name} · {input.valueType}{input.required ? " · required" : " · optional"}</legend>
        <label><span>Input source</span><select aria-label={`Source for ${input.name}`} disabled={disabled} value={selected} onChange={event => {
          const bindings = { ...value?.bindings }, inputs = { ...values }; delete bindings[input.name];
          if (event.target.value) { const [stepId, outputName] = JSON.parse(event.target.value); bindings[input.name] = { stepId, outputName }; delete inputs[input.name]; }
          onChange({ ...value!, bindings, inputs });
        }}><option value="">Literal / default</option>{binding && !candidates.some(candidate => candidate.value === selected) && <option value={selected}>Invalid source: {binding.stepId} · {binding.outputName}</option>}{candidates.map(candidate => <option key={candidate.value} value={candidate.value}>{candidate.label}</option>)}</select></label>
        {binding && !candidates.some(candidate => candidate.value === selected) && <p role="alert" className="error-text">Binding is stale or the producer is no longer guaranteed to execute first.</p>}
        {!binding && <><label className="toggle-label"><input aria-label={`Provide ${input.name}`} type="checkbox" checked={present} disabled={disabled} onChange={event => { const inputs = { ...values }; if (event.target.checked) inputs[input.name] = input.defaultValue ?? initialScalar(input.valueType); else delete inputs[input.name]; onChange({ ...value!, inputs }); }} /><span>Provide value</span></label>
        {present ? <ScalarField label={`Value for ${input.name}`} type={input.valueType} value={values[input.name]} disabled={disabled} onChange={item => onChange({ ...value!, inputs: { ...values, [input.name]: item } })} /> : <p className="muted">{input.defaultValue != null ? `Uses default: ${JSON.stringify(input.defaultValue)}` : input.required ? "A value is required before execution." : "Absent; renders as empty text."}</p>}
      </>}
      </fieldset>; })}
      {Object.keys(values).filter(name => !preview.inputs.some(input => input.name === name)).map(name => <div className="persona-conflict" key={name}><p>Undeclared input: {name} = {JSON.stringify(values[name])}</p><button type="button" disabled={disabled} onClick={() => { const inputs = { ...values }; delete inputs[name]; onChange({ ...value!, inputs }); }}>Remove {name}</button></div>)}
      {Object.keys(value?.bindings ?? {}).filter(name => !preview.inputs.some(input => input.name === name)).map(name => <p role="alert" key={name}>Undeclared bound input: {name} <button type="button" disabled={disabled} onClick={() => { const bindings = { ...value?.bindings }; delete bindings[name]; onChange({ ...value!, bindings }); }}>Remove binding {name}</button></p>)}
      <details className="optional-settings persona-preview"><summary>{current ? "Template for next save" : "Saved template"} · revision {preview.revision}</summary><pre>{preview.promptTemplate}</pre><p className="muted">Input values are substituted once. Workflow field values are captured at execution; the rendered prompt is recorded in task history.</p>{saved && current && saved.revision !== current.revision && <details><summary>Saved task revision {saved.revision}</summary><pre>{saved.promptTemplate}</pre></details>}</details>
    </>}
  </section>;
}
