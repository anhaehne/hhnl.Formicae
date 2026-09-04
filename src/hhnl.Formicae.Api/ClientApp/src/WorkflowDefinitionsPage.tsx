import { FormEvent, useCallback, useEffect, useMemo, useState } from "react";
import {
  addEdge,
  Background,
  Connection,
  Controls,
  Edge,
  Handle,
  NodeProps,
  OnConnect,
  ReactFlow,
  ReactFlowProvider,
  useEdgesState,
  useNodesState,
  useReactFlow,
  Position
} from "@xyflow/react";
import {
  ApiError,
  ConnectedRepository,
  createWorkflowDefinition,
  createWorkflowDefinitionVersion,
  getIntegration,
  IntegrationDetail,
  listIntegrations,
  WorkflowDefinitionTrigger,
  WorkflowDefinitionResponse,
  WorkflowDefinitionValidationError
} from "./api";
import {
  createDefaultDefinitionDocument,
  definitionToGraph,
  graphToDefinition,
  supportedUses,
  WorkflowStepNode,
  WorkflowStepNodeData,
  workflowSchema
} from "./workflowGraph";

type Props = {
  definitions: WorkflowDefinitionResponse[];
  loading: boolean;
  error?: string;
  saved?: string;
  canAdminister: boolean;
  onRefresh: (selectedDefinitionId?: string, selectedVersionId?: string) => Promise<void>;
  onSaved: (message: string) => void;
  onError: (message: string) => void;
};

type DraftValidationError = WorkflowDefinitionValidationError & { source?: "client" | "api" };

const nodeTypes = { workflowStep: WorkflowStepNodeComponent };

export default function WorkflowDefinitionsPage(props: Props) {
  return (
    <ReactFlowProvider>
      <WorkflowDefinitionsEditor {...props} />
    </ReactFlowProvider>
  );
}

function WorkflowDefinitionsEditor({
  definitions,
  loading,
  error,
  saved,
  canAdminister,
  onRefresh,
  onSaved,
  onError
}: Props) {
  const [selectedDefinitionId, setSelectedDefinitionId] = useState<string>();
  const [selectedVersionId, setSelectedVersionId] = useState<string>();
  const [definitionName, setDefinitionName] = useState("Custom workflow");
  const [versionNumber, setVersionNumber] = useState("");
  const [isEnabled, setIsEnabled] = useState(true);
  const [isDefault, setIsDefault] = useState(false);
  const [schema, setSchema] = useState(workflowSchema);
  const [startStepId, setStartStepId] = useState("plan");
  const [selectedNodeId, setSelectedNodeId] = useState<string>();
  const [selectedEdgeId, setSelectedEdgeId] = useState<string>();
  const [saving, setSaving] = useState(false);
  const [validationErrors, setValidationErrors] = useState<DraftValidationError[]>([]);
  const [triggers, setTriggers] = useState<WorkflowDefinitionTrigger[]>([]);
  const [integrationDetails, setIntegrationDetails] = useState<IntegrationDetail[]>([]);
  const [nodes, setNodes, onNodesChange] = useNodesState<WorkflowStepNode>([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([]);
  const { fitView } = useReactFlow();

  const selectedDefinition = useMemo(
    () => definitions.find(definition => definition.id === selectedDefinitionId),
    [definitions, selectedDefinitionId]
  );
  const selectedVersion = useMemo(
    () => selectedDefinition?.versions.find(version => version.id === selectedVersionId) ?? selectedDefinition?.versions[0],
    [selectedDefinition, selectedVersionId]
  );
  const selectedNode = useMemo(
    () => nodes.find(node => node.id === selectedNodeId),
    [nodes, selectedNodeId]
  );
  const repositoryGroups = useMemo(
    () => integrationDetails
      .map(integration => ({
        integration,
        repositories: integration.repositories
      }))
      .filter(group => group.repositories.length > 0),
    [integrationDetails]
  );

  useEffect(() => {
    let canceled = false;
    async function loadRepositories() {
      try {
        const summaries = await listIntegrations();
        const details = await Promise.all(summaries.map(summary => getIntegration(summary.id)));
        if (!canceled) {
          setIntegrationDetails(details);
        }
      } catch {
        if (!canceled) {
          setIntegrationDetails([]);
        }
      }
    }

    void loadRepositories();
    return () => {
      canceled = true;
    };
  }, []);

  useEffect(() => {
    if (definitions.length === 0 || selectedDefinitionId) {
      return;
    }

    const first = definitions[0];
    setSelectedDefinitionId(first.id);
    setSelectedVersionId(first.versions[0]?.id);
  }, [definitions, selectedDefinitionId]);

  useEffect(() => {
    if (!selectedDefinition || !selectedVersion) {
      return;
    }

    const graph = definitionToGraph(selectedVersion.definition);
    setDefinitionName(selectedDefinition.name);
    setVersionNumber("");
    setIsEnabled(selectedVersion.isEnabled);
    setIsDefault(selectedVersion.isDefault);
    setSchema(selectedVersion.definition.schema || selectedVersion.dslSchemaVersion || workflowSchema);
    setStartStepId(selectedVersion.definition.startStepId);
    setTriggers(selectedVersion.definition.triggers ?? []);
    setNodes(graph.nodes);
    setEdges(graph.edges);
    setSelectedNodeId(undefined);
    setSelectedEdgeId(undefined);
    setValidationErrors([]);
    window.setTimeout(() => fitView({ padding: 0.2 }), 0);
  }, [fitView, selectedDefinition, selectedVersion, setEdges, setNodes]);

  const onConnect = useCallback<OnConnect>((connection: Connection) => {
    if (!connection.source || !connection.target || connection.source === connection.target) {
      return;
    }

    setEdges(current => {
      const withoutExisting = current.filter(edge => edge.source !== connection.source && edge.target !== connection.target);
      return addEdge({ ...connection, id: `${connection.source}->${connection.target}` }, withoutExisting);
    });
  }, [setEdges]);

  function handleNewDefinition() {
    const graph = definitionToGraph(createDefaultDefinitionDocument());
    setSelectedDefinitionId(undefined);
    setSelectedVersionId(undefined);
    setDefinitionName("Custom workflow");
    setVersionNumber("");
    setIsEnabled(true);
    setIsDefault(false);
    setSchema(workflowSchema);
    setStartStepId("plan");
    setTriggers([]);
    setNodes(graph.nodes);
    setEdges(graph.edges);
    setSelectedNodeId(undefined);
    setSelectedEdgeId(undefined);
    setValidationErrors([]);
    window.setTimeout(() => fitView({ padding: 0.2 }), 0);
  }

  function handleSelectDefinition(definitionId: string) {
    const definition = definitions.find(item => item.id === definitionId);
    setSelectedDefinitionId(definitionId);
    setSelectedVersionId(definition?.versions[0]?.id);
  }

  function handleAddStep() {
    const base = `step${nodes.length + 1}`;
    let id = base;
    let suffix = 2;
    while (nodes.some(node => node.id === id)) {
      id = `${base}${suffix}`;
      suffix += 1;
    }

    const nextNode: WorkflowStepNode = {
      id,
      type: "workflowStep",
      position: { x: 80 + nodes.length * 40, y: 120 + nodes.length * 24 },
      data: { stepId: id, displayName: "New step", uses: "builtins.plan" }
    };
    setNodes(current => [...current, nextNode]);
    setSelectedNodeId(id);
    if (!startStepId) {
      setStartStepId(id);
    }
  }

  function handleDeleteSelectedStep() {
    if (!selectedNodeId) {
      return;
    }

    setNodes(current => current.filter(node => node.id !== selectedNodeId));
    setEdges(current => current.filter(edge => edge.source !== selectedNodeId && edge.target !== selectedNodeId));
    if (startStepId === selectedNodeId) {
      setStartStepId(nodes.find(node => node.id !== selectedNodeId)?.id ?? "");
    }
    setSelectedNodeId(undefined);
  }

  function handleDeleteSelectedEdge() {
    if (!selectedEdgeId) {
      return;
    }

    setEdges(current => current.filter(edge => edge.id !== selectedEdgeId));
    setSelectedEdgeId(undefined);
  }

  function updateSelectedNodeData(values: Partial<WorkflowStepNodeData>) {
    if (!selectedNodeId) {
      return;
    }

    setNodes(current => current.map(node => node.id === selectedNodeId ? { ...node, data: { ...node.data, ...values } } : node));
  }

  function updateSelectedNodeId(nextId: string) {
    if (!selectedNodeId || nextId === selectedNodeId) {
      updateSelectedNodeData({ stepId: nextId });
      return;
    }

    setNodes(current => current.map(node => node.id === selectedNodeId ? { ...node, id: nextId, data: { ...node.data, stepId: nextId } } : node));
    setEdges(current => current.map(edge => ({
      ...edge,
      id: edge.id.replace(selectedNodeId, nextId),
      source: edge.source === selectedNodeId ? nextId : edge.source,
      target: edge.target === selectedNodeId ? nextId : edge.target
    })));
    if (startStepId === selectedNodeId) {
      setStartStepId(nextId);
    }
    setSelectedNodeId(nextId);
  }

  function handleAddTrigger() {
    let suffix = triggers.length + 1;
    let id = `issueLabel${suffix}`;
    while (triggers.some(trigger => trigger.id === id)) {
      suffix += 1;
      id = `issueLabel${suffix}`;
    }

    setTriggers(current => [...current, {
      id,
      type: "DevOpsIssueLabel",
      enabled: true,
      repositoryIds: [],
      label: "",
      baseBranch: "",
      model: ""
    }]);
  }

  function updateTrigger(index: number, values: Partial<WorkflowDefinitionTrigger>) {
    setTriggers(current => current.map((trigger, currentIndex) => currentIndex === index ? { ...trigger, ...values } : trigger));
  }

  function toggleTriggerRepository(index: number, repositoryId: string, selected: boolean) {
    setTriggers(current => current.map((trigger, currentIndex) => {
      if (currentIndex !== index) {
        return trigger;
      }

      const nextRepositoryIds = selected
        ? Array.from(new Set([...trigger.repositoryIds, repositoryId]))
        : trigger.repositoryIds.filter(id => id !== repositoryId);
      return { ...trigger, repositoryIds: nextRepositoryIds };
    }));
  }

  function removeTrigger(index: number) {
    setTriggers(current => current.filter((_, currentIndex) => currentIndex !== index));
  }

  async function handleSave(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const clientErrors = [
      ...validateGraph(definitionName, nodes, edges, startStepId),
      ...validateTriggers(triggers)
    ];
    setValidationErrors(clientErrors);
    if (clientErrors.length > 0) {
      return;
    }

    setSaving(true);
    try {
      const definition = selectedDefinitionId
        ? selectedDefinition
        : await createWorkflowDefinition({ name: definitionName.trim() });

      if (!definition) {
        throw new Error("Workflow definition was not found.");
      }

      const savedVersion = await createWorkflowDefinitionVersion(definition.id, {
        version: versionNumber.trim() ? Number(versionNumber) : null,
        isEnabled,
        isDefault,
        definition: graphToDefinition(nodes, edges, schema.trim() || workflowSchema, startStepId, normalizeTriggers(triggers))
      });

      setSelectedDefinitionId(definition.id);
      setSelectedVersionId(savedVersion.id);
      setValidationErrors([]);
      onSaved("Workflow definition version saved.");
      await onRefresh(definition.id, savedVersion.id);
    } catch (saveError) {
      if (saveError instanceof ApiError && saveError.validationErrors.length > 0) {
        setValidationErrors(saveError.validationErrors.map(item => ({ ...item, source: "api" })));
      } else {
        onError(saveError instanceof Error ? saveError.message : "Could not save workflow definition.");
      }
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className="workflow-definitions-page">
      <form className="workflow-definition-layout" onSubmit={handleSave}>
        <section className="panel definition-list-panel">
          <div className="panel-heading">
            <h2>Definitions</h2>
            {loading ? <span className="muted">Loading</span> : null}
          </div>
          <button type="button" className="secondary-button definition-action-button" onClick={handleNewDefinition} disabled={!canAdminister}>
            New Definition
          </button>
          <div className="definition-list">
            {definitions.map(definition => (
              <button
                type="button"
                key={definition.id}
                className={`integration-row${definition.id === selectedDefinitionId ? " selected" : ""}`}
                onClick={() => handleSelectDefinition(definition.id)}
              >
                <strong>{definition.name}</strong>
                <span>{definition.versions.length} versions</span>
              </button>
            ))}
            {definitions.length === 0 ? <p className="muted">No workflow definitions found.</p> : null}
          </div>
        </section>

        <section className="panel workflow-canvas-panel">
          <div className="panel-heading">
            <h2>Workflow Graph</h2>
            <div className="button-row">
              <button type="button" className="secondary-button compact-button" onClick={handleAddStep} disabled={!canAdminister}>Add Step</button>
              <button type="button" className="secondary-button compact-button" onClick={handleDeleteSelectedStep} disabled={!selectedNodeId || !canAdminister}>Delete Step</button>
              <button type="button" className="secondary-button compact-button" onClick={handleDeleteSelectedEdge} disabled={!selectedEdgeId || !canAdminister}>Delete Edge</button>
              <button type="button" className="secondary-button compact-button" onClick={() => fitView({ padding: 0.2 })}>Fit</button>
            </div>
          </div>
          <div className="workflow-canvas">
            <ReactFlow
              nodes={nodes}
              edges={edges}
              nodeTypes={nodeTypes}
              onNodesChange={onNodesChange}
              onEdgesChange={onEdgesChange}
              onConnect={onConnect}
              onSelectionChange={({ nodes: selectedNodes, edges: selectedEdges }) => {
                setSelectedNodeId(selectedNodes[0]?.id);
                setSelectedEdgeId(selectedEdges[0]?.id);
              }}
              fitView
            >
              <Background />
              <Controls />
            </ReactFlow>
          </div>
        </section>

        <section className="panel definition-editor-panel">
          <div className="panel-heading">
            <h2>Version</h2>
            {selectedVersion ? <StatusBadge value={`v${selectedVersion.version}`} /> : <StatusBadge value="Draft" />}
          </div>

          {error ? <p className="error-text">{error}</p> : null}
          {saved ? <p className="success-text">{saved}</p> : null}
          <ValidationErrorList errors={validationErrors} />

          <label>
            <span>Definition Name</span>
            <input value={definitionName} onChange={event => setDefinitionName(event.target.value)} disabled={!canAdminister || Boolean(selectedDefinitionId)} />
          </label>
          <label>
            <span>Version</span>
            <input value={versionNumber} onChange={event => setVersionNumber(event.target.value)} type="number" min="1" placeholder="Auto" disabled={!canAdminister} />
          </label>
          <div className="form-row">
            <label>
              <span>Schema</span>
              <input value={schema} onChange={event => setSchema(event.target.value)} disabled={!canAdminister} />
            </label>
            <label>
              <span>Start Step</span>
              <select value={startStepId} onChange={event => setStartStepId(event.target.value)} disabled={!canAdminister}>
                {nodes.map(node => <option key={node.id} value={node.id}>{node.data.stepId || node.id}</option>)}
              </select>
            </label>
          </div>
          <div className="button-row definition-toggle-row">
            <label className="toggle-label">
              <input type="checkbox" checked={isEnabled} onChange={event => setIsEnabled(event.target.checked)} disabled={!canAdminister} />
              <span>Enabled</span>
            </label>
            <label className="toggle-label">
              <input type="checkbox" checked={isDefault} onChange={event => setIsDefault(event.target.checked)} disabled={!canAdminister} />
              <span>Default</span>
            </label>
          </div>

          <section className="settings-section">
            <h3>Selected Step</h3>
            {selectedNode ? (
              <>
                <label>
                  <span>Step ID</span>
                  <input value={selectedNode.data.stepId} onChange={event => updateSelectedNodeId(event.target.value)} disabled={!canAdminister} />
                </label>
                <label>
                  <span>Display Name</span>
                  <input value={selectedNode.data.displayName} onChange={event => updateSelectedNodeData({ displayName: event.target.value })} disabled={!canAdminister} />
                </label>
                <label>
                  <span>Built-in Task</span>
                  <select value={selectedNode.data.uses} onChange={event => updateSelectedNodeData({ uses: event.target.value })} disabled={!canAdminister}>
                    {supportedUses.map(uses => <option key={uses} value={uses}>{uses}</option>)}
                  </select>
                </label>
                <button type="button" className="secondary-button" onClick={() => setStartStepId(selectedNode.id)} disabled={!canAdminister}>
                  Set as Start Step
                </button>
              </>
            ) : (
              <p className="muted">Select a step node to edit it.</p>
            )}
          </section>

          <section className="settings-section">
            <div className="section-heading-row">
              <h3>Triggers</h3>
              <button type="button" className="secondary-button compact-button" onClick={handleAddTrigger} disabled={!canAdminister}>Add</button>
            </div>
            <div className="trigger-list">
              {triggers.map((trigger, index) => (
                <div className="trigger-row" key={`${trigger.id}-${index}`}>
                  <div className="form-row">
                    <label>
                      <span>ID</span>
                      <input value={trigger.id} onChange={event => updateTrigger(index, { id: event.target.value })} disabled={!canAdminister} />
                    </label>
                    <label>
                      <span>Type</span>
                      <select value={trigger.type} onChange={event => updateTrigger(index, { type: event.target.value as WorkflowDefinitionTrigger["type"] })} disabled={!canAdminister}>
                        <option value="DevOpsIssueLabel">DevOpsIssueLabel</option>
                      </select>
                    </label>
                  </div>
                  <label className="toggle-label">
                    <input type="checkbox" checked={trigger.enabled} onChange={event => updateTrigger(index, { enabled: event.target.checked })} disabled={!canAdminister} />
                    <span>Enabled</span>
                  </label>
                  <label>
                    <span>Label</span>
                    <input value={trigger.label ?? ""} onChange={event => updateTrigger(index, { label: event.target.value })} disabled={!canAdminister} />
                  </label>
                  <div className="trigger-repository-select">
                    {repositoryGroups.map(group => (
                      <fieldset key={group.integration.id}>
                        <legend>{group.integration.providerType} / {group.integration.displayName}</legend>
                        {group.repositories.map(repository => (
                          <label className="toggle-label" key={repository.id}>
                            <input
                              type="checkbox"
                              checked={trigger.repositoryIds.includes(repository.id)}
                              onChange={event => toggleTriggerRepository(index, repository.id, event.target.checked)}
                              disabled={!canAdminister}
                            />
                            <span>{repositoryLabel(repository)}</span>
                          </label>
                        ))}
                      </fieldset>
                    ))}
                    {repositoryGroups.length === 0 ? <p className="muted">No connected repositories.</p> : null}
                  </div>
                  <div className="form-row">
                    <label>
                      <span>Base Branch</span>
                      <input value={trigger.baseBranch ?? ""} onChange={event => updateTrigger(index, { baseBranch: event.target.value })} placeholder="Repository default" disabled={!canAdminister} />
                    </label>
                    <label>
                      <span>Model</span>
                      <input value={trigger.model ?? ""} onChange={event => updateTrigger(index, { model: event.target.value })} placeholder="Default AI model" disabled={!canAdminister} />
                    </label>
                  </div>
                  <button type="button" className="secondary-button danger-button compact-button" onClick={() => removeTrigger(index)} disabled={!canAdminister}>
                    Remove
                  </button>
                </div>
              ))}
              {triggers.length === 0 ? <p className="muted">No triggers configured.</p> : null}
            </div>
          </section>

          <button type="submit" className="primary-button" disabled={saving || !canAdminister}>
            {saving ? "Saving" : "Save Version"}
          </button>
        </section>
      </form>
    </section>
  );
}

function WorkflowStepNodeComponent({ data, selected }: NodeProps<WorkflowStepNode>) {
  return (
    <div className={`workflow-step-node${selected ? " selected" : ""}`}>
      <Handle type="target" position={Position.Left} />
      <strong>{data.displayName}</strong>
      <span className="mono">{data.stepId}</span>
      <span>{data.uses}</span>
      <Handle type="source" position={Position.Right} />
    </div>
  );
}

function ValidationErrorList({ errors }: { errors: DraftValidationError[] }) {
  if (errors.length === 0) {
    return null;
  }

  return (
    <div className="validation-error-list">
      {errors.map((error, index) => (
        <p className="error-text" key={`${error.code}-${index}`}>
          {error.path ? <strong>{error.path}: </strong> : null}
          {error.message}
          {error.code ? <span className="validation-code"> {error.code}</span> : null}
        </p>
      ))}
    </div>
  );
}

function validateGraph(name: string, nodes: WorkflowStepNode[], edges: Edge[], startStepId: string): DraftValidationError[] {
  const errors: DraftValidationError[] = [];
  const ids = nodes.map(node => node.data.stepId || node.id);
  const uniqueIds = new Set(ids);
  const outgoingCounts = countBy(edges.map(edge => edge.source));
  const incomingCounts = countBy(edges.map(edge => edge.target));
  const terminalCount = nodes.filter(node => !edges.some(edge => edge.source === node.id)).length;

  if (!name.trim()) {
    errors.push({ code: "definition.name.required", message: "Definition name is required.", path: "name", source: "client" });
  }
  if (nodes.length === 0) {
    errors.push({ code: "definition.steps.required", message: "At least one step is required.", path: "steps", source: "client" });
  }
  if (!startStepId || !nodes.some(node => node.id === startStepId)) {
    errors.push({ code: "definition.startStepId.invalid", message: "Start step must reference an existing step.", path: "startStepId", source: "client" });
  }
  if (ids.some(id => !id.trim())) {
    errors.push({ code: "definition.step.id.required", message: "Node ids must be non-empty.", path: "steps[].id", source: "client" });
  }
  if (uniqueIds.size !== ids.length) {
    errors.push({ code: "definition.step.id.duplicate", message: "Node ids must be unique.", path: "steps[].id", source: "client" });
  }
  for (const [source, count] of outgoingCounts) {
    if (count > 1) {
      errors.push({ code: "definition.graph.outgoing.invalid", message: `Step '${source}' has more than one outgoing edge.`, path: "steps[].nextStepId", source: "client" });
    }
  }
  for (const [target, count] of incomingCounts) {
    if (count > 1 || (target === startStepId && count > 0)) {
      errors.push({ code: "definition.graph.incoming.invalid", message: `Step '${target}' has invalid incoming edges.`, path: "steps", source: "client" });
    }
  }
  if (nodes.length > 0 && terminalCount !== 1) {
    errors.push({ code: "definition.graph.terminal.invalid", message: "Only one terminal step should exist.", path: "steps", source: "client" });
  }

  return errors;
}

function validateTriggers(triggers: WorkflowDefinitionTrigger[]): DraftValidationError[] {
  const errors: DraftValidationError[] = [];
  const ids = triggers.map(trigger => trigger.id);
  const uniqueIds = new Set(ids);
  if (ids.some(id => !id.trim())) {
    errors.push({ code: "definition.trigger.id.required", message: "Trigger ids must be non-empty.", path: "triggers[].id", source: "client" });
  }
  if (uniqueIds.size !== ids.length) {
    errors.push({ code: "definition.trigger.id.duplicate", message: "Trigger ids must be unique.", path: "triggers[].id", source: "client" });
  }

  triggers.forEach((trigger, index) => {
    if (!trigger.enabled || trigger.type !== "DevOpsIssueLabel") {
      return;
    }

    if (!trigger.label?.trim()) {
      errors.push({ code: "definition.trigger.label.required", message: `Trigger ${index + 1} requires a label.`, path: "triggers[].label", source: "client" });
    }
    if (trigger.repositoryIds.length === 0) {
      errors.push({ code: "definition.trigger.repositories.required", message: `Trigger ${index + 1} requires at least one repository.`, path: "triggers[].repositoryIds", source: "client" });
    }
  });

  return errors;
}

function normalizeTriggers(triggers: WorkflowDefinitionTrigger[]): WorkflowDefinitionTrigger[] {
  return triggers.map(trigger => ({
    id: trigger.id.trim(),
    type: trigger.type,
    enabled: trigger.enabled,
    repositoryIds: trigger.repositoryIds,
    label: trigger.label?.trim() || null,
    baseBranch: trigger.baseBranch?.trim() || null,
    model: trigger.model?.trim() || null
  }));
}

function repositoryLabel(repository: ConnectedRepository) {
  return `${repository.owner}/${repository.name} (${repository.defaultBranch})`;
}

function countBy(values: string[]) {
  const counts = new Map<string, number>();
  for (const value of values) {
    counts.set(value, (counts.get(value) ?? 0) + 1);
  }

  return counts;
}

function StatusBadge({ value }: { value: string }) {
  return <span className={`status-badge status-${value.toLowerCase()}`}>{value}</span>;
}
