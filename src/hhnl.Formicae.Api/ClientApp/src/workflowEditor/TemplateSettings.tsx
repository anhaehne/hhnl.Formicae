import type { CustomTaskDefinition, CustomTaskInputDefinition, WorkflowTemplateSettings } from "../api";
import type { Edge } from "@xyflow/react";
import { eligibleProducer, type WorkflowStepNode } from "../workflowGraph";
import { outputsFor } from "../workflowData";

type Props = { value?: WorkflowTemplateSettings | null; nodes: WorkflowStepNode[]; edges: Edge[]; stepId: string; start: string; tasks: CustomTaskDefinition[]; disabled: boolean; onChange: (value: WorkflowTemplateSettings) => void };
export function TemplateSettings({ value, nodes, edges, stepId, start, tasks, disabled, onChange }: Props) {
  const settings = value ?? { template: "", inputs: [] };
  const updateInput = (index: number, patch: Partial<CustomTaskInputDefinition>) => {
    const previous = settings.inputs[index], bindings = { ...settings.bindings };
    const binding = bindings[previous.name]; delete bindings[previous.name];
    const name = patch.name ?? previous.name;
    if (binding && !patch.valueType) bindings[name] = binding;
    onChange({ ...settings, inputs: settings.inputs.map((input, i) => i === index ? { ...input, ...patch } : input), bindings });
  };
  const add = () => {
    let suffix = 1; while (settings.inputs.some(input => input.name === `input${suffix}`)) suffix++;
    onChange({ ...settings, inputs: [...settings.inputs, { name: `input${suffix}`, valueType: "string", required: true }] });
  };
  return <section className="editor-property-section"><h4>Template</h4>
    <label><span>Scriban template</span><textarea aria-label="Scriban template" rows={10} maxLength={16000} disabled={disabled} value={settings.template} placeholder={'Hello {{ inputs.name }}!'} onChange={event => onChange({ ...settings, template: event.target.value })} /></label>
    <p className="muted">Use {"{{ inputs.name }}"} to insert a connected value. Conditions: {"{{ if inputs.ready }}Ready{{ else }}Waiting{{ end }}"}. Output: text (string).</p>
    <a href="https://scriban.github.io/docs/language/" target="_blank" rel="noreferrer">Scriban language reference</a>
    <h4>Data inputs</h4>
    <p className="muted">Add any number of named inputs, then connect their ports on the canvas or choose sources below. Each input needs a connection.</p>
    <button type="button" className="secondary-button" disabled={disabled} onClick={add}>Add template input</button>
    {settings.inputs.map((input, index) => {
      const binding = settings.bindings?.[input.name];
      const producers = nodes.filter(node => eligibleProducer(nodes, edges, start, node.id, stepId)).flatMap(node => outputsFor(node, tasks).filter(output => output.valueType === input.valueType).map(output => ({ node, output })));
      const sourceValue = binding ? JSON.stringify([binding.stepId, binding.outputName]) : "";
      const sourceKnown = producers.some(({ node, output }) => sourceValue === JSON.stringify([node.id, output.name]));
      return <fieldset className="custom-task-schema" key={index}><legend>Input {index + 1}</legend>
        <label><span>Name</span><input aria-label={`Template input ${index + 1} name`} disabled={disabled} value={input.name} pattern="[A-Za-z][A-Za-z0-9_]{0,63}" maxLength={64} onChange={event => updateInput(index, { name: event.target.value })} /></label>
        <label><span>Type</span><select aria-label={`Template input ${index + 1} type`} disabled={disabled} value={input.valueType} onChange={event => updateInput(index, { valueType: event.target.value as CustomTaskInputDefinition["valueType"] })}><option value="string">String</option><option value="number">Number</option><option value="boolean">Boolean</option></select></label>
        <label><span>Source for {input.name}</span><select aria-label={`Source for ${input.name}`} disabled={disabled} value={sourceValue} onChange={event => {
          const bindings = { ...settings.bindings }; delete bindings[input.name];
          if (event.target.value) { const [stepId, outputName] = JSON.parse(event.target.value); bindings[input.name] = { stepId, outputName }; }
          onChange({ ...settings, bindings });
        }}><option value="">Not connected</option>{binding && !sourceKnown && <option value={sourceValue}>Unavailable: {binding.stepId} → {binding.outputName}</option>}{producers.map(({ node, output }) => <option key={`${node.id}:${output.name}`} value={JSON.stringify([node.id, output.name])}>{node.data.displayName} → {output.name} ({output.valueType})</option>)}</select></label>
        <button type="button" disabled={disabled} onClick={() => { const bindings = { ...settings.bindings }; delete bindings[input.name]; onChange({ ...settings, inputs: settings.inputs.filter((_, i) => i !== index), bindings }); }}>Remove input {index + 1}</button>
      </fieldset>;
    })}
  </section>;
}
