import type { ComponentProps } from "react";
import type { AgentTaskDefinition, WorkflowCustomTaskSettings } from "../api";
import { CustomTaskSettings } from "./CustomTaskSettings";
import { CustomTaskSchema, CustomTaskOutputSchema } from "./CustomTaskSchema";

export function AgentTaskSettings(props: ComponentProps<typeof CustomTaskSettings>) {
  const { value, tasks, disabled, onChange, stepId } = props;
  const definition: AgentTaskDefinition = value?.definition ?? { promptTemplate: "", inputs: [], outputs: [], runner: { kind: "agent", timeoutSeconds: 1800 } };
  const configure = (next: AgentTaskDefinition, reset = false) => onChange({ ...value, taskId: `agent:${stepId}`, definition: next, inputs: reset ? {} : value?.inputs, bindings: reset ? {} : value?.bindings, snapshot: undefined });
  const preview: WorkflowCustomTaskSettings = { ...value, taskId: `agent:${stepId}`, snapshot: { ...definition, id: `agent:${stepId}`, revision: 1, name: "Agent task", description: "" } };
  return <>
    <section className="editor-property-section"><h4>Agent task</h4>
      <label><span>Prefill from custom task</span><select aria-label="Prefill from custom task" disabled={disabled} value="" onChange={event => {
        const task = tasks.find(item => item.id === event.target.value);
        if (task) configure(structuredClone({ promptTemplate: task.promptTemplate, inputs: task.inputs, outputs: task.outputs, runner: task.runner }), true);
      }}><option value="">Choose a template</option>{tasks.map(task => <option key={task.id} value={task.id}>{task.name}</option>)}</select></label>
      <p className="muted">Copies the prompt, schemas and timeout into this node. You can edit them independently. Selecting a template replaces these settings and clears input values and bindings.</p>
      <label><span>Prompt template</span><textarea aria-label="Agent prompt template" disabled={disabled} maxLength={16000} value={definition.promptTemplate} onChange={event => configure({ ...definition, promptTemplate: event.target.value })} /></label>
      <label><span>Timeout seconds</span><input aria-label="Agent timeout seconds" type="number" min={1} max={3600} disabled={disabled} value={definition.runner.timeoutSeconds} onChange={event => configure({ ...definition, runner: { kind: "agent", timeoutSeconds: Number(event.target.value) } })} /></label>
      <CustomTaskSchema inputs={definition.inputs} disabled={disabled} onChange={inputs => configure({ ...definition, inputs })} />
      <CustomTaskOutputSchema outputs={definition.outputs ?? []} disabled={disabled} onChange={outputs => configure({ ...definition, outputs })} />
    </section>
    <CustomTaskSettings {...props} inline value={preview} savedSnapshot={undefined} onChange={next => onChange({ ...next, definition, snapshot: undefined })} />
  </>;
}
