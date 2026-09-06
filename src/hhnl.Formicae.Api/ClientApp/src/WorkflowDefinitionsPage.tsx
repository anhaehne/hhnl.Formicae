import { FormEvent, useCallback, useEffect, useMemo, useState } from "react";
import { StepModelSettings } from "./StepModelSettings";
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
  WorkflowTriggerNodeSettings,
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
  workflowSchema, toNodeDefinition, triggerUses, loopUses
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
  const [creatingDefinition, setCreatingDefinition] = useState(false);
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
  const [newStepType, setNewStepType] = useState("builtins.plan");
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
    if (definitions.length === 0 || selectedDefinitionId || creatingDefinition) {
      return;
    }

    const first = definitions[0];
    setSelectedDefinitionId(first.id);
    setSelectedVersionId(first.versions[0]?.id);
  }, [definitions, selectedDefinitionId, creatingDefinition]);

  useEffect(() => {
    if (!selectedDefinition || !selectedVersion) {
      return;
    }

    const draft = toNodeDefinition(selectedVersion.definition);
    const graph = definitionToGraph(draft);
    setDefinitionName(selectedDefinition.name);
    setVersionNumber("");
    setIsEnabled(selectedVersion.isEnabled);
    setIsDefault(selectedVersion.isDefault);
    setSchema(workflowSchema);
    setStartStepId(draft.startStepId);

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
      const withoutExisting = current.filter(edge => !(edge.source === connection.source && edge.sourceHandle === connection.sourceHandle));
      return addEdge({ ...connection, id: `${connection.source}:${connection.sourceHandle}` }, withoutExisting);
    });
  }, [setEdges]);

  function handleNewDefinition() {
    const graph = definitionToGraph(createDefaultDefinitionDocument());
    setCreatingDefinition(true);
    setSelectedDefinitionId(undefined);
    setSelectedVersionId(undefined);
    setDefinitionName("Custom workflow");
    setVersionNumber("");
    setIsEnabled(true);
    setIsDefault(false);
    setSchema(workflowSchema);
    setStartStepId("plan");

    setNodes(graph.nodes);
    setEdges(graph.edges);
    setSelectedNodeId(undefined);
    setSelectedEdgeId(undefined);
    setValidationErrors([]);
    window.setTimeout(() => fitView({ padding: 0.2 }), 0);
  }

  function handleSelectDefinition(definitionId: string) {
    const definition = definitions.find(item => item.id === definitionId);
    setCreatingDefinition(false);
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
      position: { x: 80 + (nodes.length % 3) * 280, y: 80 + Math.floor(nodes.length / 3) * 200 },
      data: { stepId: id, displayName: newStepType === loopUses ? "Loop" : newStepType === triggerUses ? "Trigger" : "New task", uses: newStepType,
        loop: newStepType === loopUses ? { bodyStepId: "", repeatCount: 2, maxIterations: 2 } : undefined,
        trigger: newStepType === triggerUses ? { type: "DevOpsIssueLabel", enabled: true, repositoryIds: [], label: "" } : undefined }
    };
    setNodes(current => [...current, nextNode]);
    setSelectedNodeId(id);
    if (!startStepId && newStepType !== triggerUses) {
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
      setStartStepId(nodes.find(node => node.id !== selectedNodeId && node.data.uses !== triggerUses)?.id ?? "");
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

  function updateTrigger(values: Partial<WorkflowTriggerNodeSettings>) {
    if (selectedNode?.data.trigger) updateSelectedNodeData({ trigger: { ...selectedNode.data.trigger, ...values } });
  }

  function connectionPicker(handle: string, label: string) {
    if (!selectedNode) return null;
    const connection = edges.find(edge => edge.source === selectedNode.id && (edge.sourceHandle || "next") === handle);
    return <label><span>{label}</span><select aria-label={label} disabled={!canAdminister}
      value={connection ? JSON.stringify([connection.target, connection.targetHandle || "input"]) : ""}
      onChange={event => {
        const value = event.target.value;
        setEdges(current => {
          const remaining = current.filter(edge => !(edge.source === selectedNode.id && (edge.sourceHandle || "next") === handle));
          if (!value) return remaining;
          const [target, targetHandle] = JSON.parse(value) as [string, string];
          return [...remaining, { id: `${selectedNode.id}:${handle}`, source: selectedNode.id, sourceHandle: handle, target, targetHandle,
            label: targetHandle === "return" ? "Return" : handle === "body" ? "Body" : handle === "exit" ? "Exit" : undefined }];
        });
      }}>
      <option value="">Not connected</option>
      {nodes.filter(node => node.id !== selectedNode.id && node.data.uses !== triggerUses && (handle !== "body" || node.data.uses !== loopUses)).flatMap(node => [
        <option key={node.id} value={JSON.stringify([node.id, "input"])}>{node.data.displayName} ({node.id})</option>,
        ...(node.data.uses === loopUses && selectedNode.data.uses !== triggerUses && selectedNode.data.uses !== loopUses
          ? [<option key={`${node.id}:return`} value={JSON.stringify([node.id, "return"])}>Return to {node.data.displayName} ({node.id})</option>] : [])
      ])}
    </select></label>;
  }

  async function handleSave(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const clientErrors = validateGraph(definitionName, nodes, edges, startStepId);
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
        definition: graphToDefinition(nodes, edges, schema.trim() || workflowSchema, startStepId)
      });

      setCreatingDefinition(false);
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
              <select aria-label="New step type" value={newStepType} onChange={event => setNewStepType(event.target.value)} disabled={!canAdminister}>
                <option value="builtins.plan">Task</option><option value={triggerUses}>Trigger</option><option value={loopUses}>Loop</option>
              </select>
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
              isValidConnection={connection => {
                const source = nodes.find(node => node.id === connection.source);
                const target = nodes.find(node => node.id === connection.target);
                return !!source && !!target && source.id !== target.id && target.data.uses !== triggerUses
                  && !(connection.sourceHandle === "body" && target.data.uses === loopUses)
                  && (connection.targetHandle !== "return" || (target.data.uses === loopUses && source.data.uses !== loopUses && source.data.uses !== triggerUses));
              }}
              onSelectionChange={({ nodes: selectedNodes, edges: selectedEdges }) => {
                setSelectedNodeId(selectedNodes[0]?.id);
                setSelectedEdgeId(selectedEdges[0]?.id);
              }}
              minZoom={0.1}
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
              <input value={schema} readOnly />
            </label>
            <label>
              <span>Start Step</span>
              <select value={startStepId} onChange={event => setStartStepId(event.target.value)} disabled={!canAdminister}>
                {nodes.filter(node => node.data.uses !== triggerUses).map(node => <option key={node.id} value={node.id}>{node.data.stepId || node.id}</option>)}
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
                {selectedNode.data.uses !== triggerUses && selectedNode.data.uses !== loopUses ? <>
                  <label><span>Built-in Task</span><select value={selectedNode.data.uses} onChange={event => updateSelectedNodeData({ uses: event.target.value })} disabled={!canAdminister}>
                    {supportedUses.map(uses => <option key={uses} value={uses}>{uses}</option>)}
                  </select></label>
                  {selectedNode.data.uses !== "builtins.create-pull-request" ? <StepModelSettings key={selectedNode.id} aiSettingsId={selectedNode.data.aiSettingsId} model={selectedNode.data.model} disabled={!canAdminister} onChange={updateSelectedNodeData} /> : null}
                </> : <p>{selectedNode.data.uses === triggerUses ? "Trigger" : "Loop"} node</p>}
                {selectedNode.data.loop ? <>
                  <label><span>Repeat count</span><input type="number" min="1" value={selectedNode.data.loop.repeatCount} disabled={!canAdminister} onChange={event => updateSelectedNodeData({ loop: { ...selectedNode.data.loop!, repeatCount: Number(event.target.value) } })} /></label>
                  <label><span>Maximum iterations</span><input type="number" min="1" value={selectedNode.data.loop.maxIterations} disabled={!canAdminister} onChange={event => updateSelectedNodeData({ loop: { ...selectedNode.data.loop!, maxIterations: Number(event.target.value) } })} /></label>
                  <label><span>Timeout seconds (optional)</span><input type="number" min="1" value={selectedNode.data.loop.timeoutSeconds ?? ""} disabled={!canAdminister} onChange={event => updateSelectedNodeData({ loop: { ...selectedNode.data.loop!, timeoutSeconds: event.target.value ? Number(event.target.value) : null } })} /></label>
                  {connectionPicker("body", "Loop body")}{connectionPicker("exit", "Loop exit")}
                  <p className="muted">Connect the final body task to this loop's Return input.</p>
                </> : connectionPicker("next", "Next step")}
                {selectedNode.data.trigger ? <>
                  <p>Event: Issue label added</p>
                  <label className="toggle-label"><input type="checkbox" checked={selectedNode.data.trigger.enabled} disabled={!canAdminister} onChange={event => updateTrigger({ enabled: event.target.checked })} /><span>Trigger enabled</span></label>
                  <label><span>Label</span><input value={selectedNode.data.trigger.label ?? ""} disabled={!canAdminister} onChange={event => updateTrigger({ label: event.target.value })} /></label>
                  {repositoryGroups.map(group => <fieldset key={group.integration.id}><legend>{group.integration.displayName}</legend>
                    {group.repositories.map(repository => <label className="toggle-label" key={repository.id}>
                      <input type="checkbox" checked={selectedNode.data.trigger!.repositoryIds.includes(repository.id)} disabled={!canAdminister} onChange={event => updateTrigger({ repositoryIds: event.target.checked
                        ? [...selectedNode.data.trigger!.repositoryIds, repository.id] : selectedNode.data.trigger!.repositoryIds.filter(id => id !== repository.id) })} />
                      <span>{repositoryLabel(repository)}</span></label>)}
                  </fieldset>)}
                  {repositoryGroups.length === 0 ? <p className="muted">No connected repositories.</p> : null}
                  <label><span>Base Branch</span><input value={selectedNode.data.trigger.baseBranch ?? ""} disabled={!canAdminister} placeholder="Repository default" onChange={event => updateTrigger({ baseBranch: event.target.value })} /></label>
                  <label><span>Workflow model</span><input value={selectedNode.data.trigger.model ?? ""} disabled={!canAdminister} placeholder="Default AI model" onChange={event => updateTrigger({ model: event.target.value })} /></label>
                </> : null}
                <button type="button" className="secondary-button" onClick={() => setStartStepId(selectedNode.id)} disabled={!canAdminister || selectedNode.data.uses === triggerUses}>
                  Set as Start Step
                </button>
              </>
            ) : (
              <p className="muted">Select a step node to edit it.</p>
            )}
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
      {data.uses !== triggerUses ? <Handle id="input" type="target" position={Position.Left} /> : null}
      {data.uses === loopUses ? <><span>Return</span><Handle id="return" type="target" position={Position.Top} /></> : null}
      <strong>{data.displayName}</strong>
      <span className="mono">{data.stepId}</span>
      <span>{data.uses === loopUses ? `Repeat ${data.loop?.repeatCount ?? 0} times` : data.uses === triggerUses ? `Label: ${data.trigger?.label || "unset"}` : data.uses}</span>
      {data.uses === loopUses ? <><span>Body / Exit</span><Handle id="body" type="source" position={Position.Right} style={{ top: "35%" }} /><Handle id="exit" type="source" position={Position.Right} style={{ top: "75%" }} /></>
        : <Handle id="next" type="source" position={Position.Right} />}
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
  const error = (message: string) => errors.push({ code: "definition.graph.invalid", message, source: "client" });
  if (!name.trim()) error("Definition name is required.");
  if (!nodes.length) error("At least one task is required.");
  if (!nodes.some(node => node.id === startStepId && node.data.uses !== triggerUses)) error("Manual start must reference a task or loop.");
  const ids = nodes.map(node => node.id);
  if (ids.some(id => !id.trim()) || new Set(ids).size !== ids.length) error("Node IDs must be nonempty and unique.");
  for (const [source, count] of countBy(edges.map(edge => `${edge.source}:${edge.sourceHandle || "next"}`)))
    if (count > 1) error(`Output '${source}' has more than one connection.`);
  return errors;
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
