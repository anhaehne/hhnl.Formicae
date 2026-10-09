import { useEffect, useState } from "react";
import type { Edge } from "@xyflow/react";
import { listIntegrations, getIntegration, type IntegrationDetail, type WorkflowWaitSettings, type CustomTaskDefinition } from "../api";
import { eligibleProducer, type WorkflowStepNode } from "../workflowGraph";

export function WaitSettings({ value, nodes, edges, stepId, start, tasks, disabled, onChange }: {
  value?: WorkflowWaitSettings | null; nodes: WorkflowStepNode[]; edges: Edge[]; stepId: string; start: string;
  tasks: CustomTaskDefinition[]; disabled: boolean; onChange: (value: WorkflowWaitSettings) => void;
}) {
  const [integrations, setIntegrations] = useState<IntegrationDetail[]>([]), [error, setError] = useState("");
  useEffect(() => { listIntegrations().then(items => Promise.all(items.filter(item => item.providerType === "GitHub").map(item => getIntegration(item.id)))).then(setIntegrations).catch(() => setError("Could not load connected GitHub repositories.")); }, []);
  const candidates = nodes.filter(node => eligibleProducer(nodes, edges, start, node.id, stepId)).flatMap(node => {
    const schema = node.data.customTask?.definition ?? tasks.find(task => task.id === node.data.customTask?.taskId) ?? node.data.customTask?.snapshot;
    return (schema?.outputs ?? []).filter(output => output.valueType === "number").map(output => ({ value: JSON.stringify([node.id, output.name]), label: `${node.data.displayName} · ${output.name}` }));
  });
  const binding = value?.issueNumberBinding, selected = binding ? JSON.stringify([binding.stepId, binding.outputName]) : "";
  return <section className="editor-property-section"><h4>Wait for issue comment</h4>
    <p className="muted">The first new comment continues this execution once. Further comments are ignored until the flow enters a wait again.</p>
    {error && <p role="alert">{error}</p>}
    <label><span>GitHub repository</span><select aria-label="GitHub repository" disabled={disabled} value={value?.repositoryId ?? ""} onChange={event => onChange({ ...value, repositoryId: event.target.value || null })}>
      <option value="">Execution repository</option>{integrations.flatMap(integration => integration.repositories.map(repository => <option key={repository.id} value={repository.id}>{repository.owner}/{repository.name}</option>))}
    </select></label>
    <label><span>Issue number source</span><select aria-label="Issue number source" disabled={disabled} value={selected} onChange={event => {
      const binding = event.target.value ? JSON.parse(event.target.value) as [string, string] : null;
      onChange({ ...value, issueNumber: binding ? undefined : 1, issueNumberBinding: binding ? { stepId: binding[0], outputName: binding[1] } : null });
    }}><option value="">Literal</option>{binding && !candidates.some(item => item.value === selected) && <option value={selected}>Unavailable: {binding.stepId} · {binding.outputName}</option>}{candidates.map(item => <option key={item.value} value={item.value}>{item.label}</option>)}</select></label>
    {!binding && <label><span>Issue number</span><input aria-label="Issue number" type="number" min="1" step="1" disabled={disabled} value={value?.issueNumber ?? ""} onChange={event => onChange({ ...value, issueNumber: event.target.value ? Number(event.target.value) : null, issueNumberBinding: null })} /></label>}
  </section>;
}
