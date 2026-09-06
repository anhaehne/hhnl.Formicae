import { PersonaPicker } from "./workflowEditor/PersonaPicker";
import { StepIcon } from "./workflowEditor/StepIcon";
import { useEffect, useId, useMemo, useRef, useState } from "react";
import { useBlocker, useBeforeUnload } from "react-router-dom";
import { Background, Controls, MiniMap, ReactFlow, ReactFlowProvider, useReactFlow, useOnViewportChange, useNodesInitialized, MarkerType, type Connection, type Edge } from "@xyflow/react";
import { ApiError, listPersonas, type Persona, createWorkflowDefinition, createWorkflowDefinitionVersion, validateWorkflowDefinition, type WorkflowDefinitionResponse, type WorkflowDefinitionVersionResponse, type WorkflowDefinitionValidationError } from "./api";
import { createDefaultDefinitionDocument, definitionToGraph, graphToDefinition, loopUses, triggerUses, parallelUses, decisionUses, workflowSchema, toNodeDefinition, type WorkflowStepNode } from "./workflowGraph";
import { useEditorState, type EditorDraft } from "./workflowEditor/state";
import { catalog } from "./workflowEditor/catalog";
import { arrange } from "./workflowEditor/layout";
import { ToolbarIcon } from "./workflowEditor/ToolbarIcon";
import { Inspector } from "./workflowEditor/Inspector";
import { NodeActions, WorkflowNode } from "./workflowEditor/Node";

type Props = { definitions: WorkflowDefinitionResponse[]; loading: boolean; error?: string; saved?: string; canAdminister: boolean; onRefresh: (definitionId?: string, versionId?: string) => Promise<void>; onSaved: (message: string) => void; onError: (message: string) => void };
const nodeTypes = { workflowStep: WorkflowNode };
const initial: EditorDraft = { name: "Custom workflow", version: "", enabled: true, isDefault: false, start: "plan", ...definitionToGraph(createDefaultDefinitionDocument()) };
const makeEdge = (source: string, sourceHandle: string, target: string, targetHandle = "input"): Edge => ({ id: `${source}:${sourceHandle}`, source, sourceHandle, target, targetHandle, markerEnd: { type: MarkerType.ArrowClosed }, label: sourceHandle === "true" ? "True" : sourceHandle === "false" ? "False" : targetHandle === "join" ? "Join" : sourceHandle.startsWith("branch:") ? `Branch ${Number(sourceHandle.slice(7)) + 1}` : targetHandle === "return" ? "Return" : sourceHandle === "body" ? "Body" : sourceHandle === "exit" ? "Exit" : undefined, style: targetHandle === "join" ? { strokeDasharray: "3 3", stroke: "#62509b" } : targetHandle === "return" ? { strokeDasharray: "6 4", stroke: "#986c26" } : undefined });
export default function WorkflowDefinitionsPage(props: Props) { return <ReactFlowProvider><Editor {...props} /></ReactFlowProvider>; }

function Editor({ definitions, loading, error, canAdminister, onRefresh, onSaved, onError }: Props) {
  const state = useEditorState(initial), { draft } = state;
  const [personas, setPersonas] = useState<Persona[]>([]), [personaError, setPersonaError] = useState("");
  const refreshPersonas = () => listPersonas().then(items => { setPersonas(items); setPersonaError(""); }).catch(() => setPersonaError("Could not load personas. Existing selections are preserved."));
  useEffect(() => { void refreshPersonas(); }, []);
  const [definitionId, setDefinitionId] = useState<string>();
  const [versionId, setVersionId] = useState<string>();
  const [selected, setSelected] = useState<string[]>([]), [selectedEdges, setSelectedEdges] = useState<string[]>([]);
  const [inspector, setInspector] = useState(true), [settings, setSettings] = useState(false), [menu, setMenu] = useState(false), [switcher, setSwitcher] = useState(false), [problems, setProblems] = useState(false), [miniMap, setMiniMap] = useState(false);
  const [query, setQuery] = useState(""), [workflowQuery, setWorkflowQuery] = useState(""), [nodeQuery, setNodeQuery] = useState("");
  const [context, setContext] = useState<{ source: string; port: string }>();
  const [saving, setSaving] = useState(false), [loadingDraft, setLoadingDraft] = useState(false), [arranging, setArranging] = useState(false);
  const [errors, setErrors] = useState<WorkflowDefinitionValidationError[]>([]), [validationFailure, setValidationFailure] = useState("");
  const [notice, setNotice] = useState("");
  const [pending, setPending] = useState<{ message: string; label: string; action: () => void }>();
  const initialized = useRef(false), generation = useRef(0), validationGeneration = useRef(0);
  const { fitView, setViewport, getViewport, screenToFlowPosition, getNode } = useReactFlow();
  const nodesInitialized = useNodesInitialized();
  const [measurements, setMeasurements] = useState<Record<string, { width: number; height: number }>>({});
  const [focusNodeId, setFocusNodeId] = useState<string>();
  const canvas = useRef<HTMLDivElement>(null);
  const [zoom, setZoom] = useState(100);
  useOnViewportChange({ onChange: viewport => setZoom(Math.round(viewport.zoom * 100)) });
  const editable = canAdminister && !loadingDraft && !arranging;
  const blocker = useBlocker(state.dirty && canAdminister);
  useBeforeUnload(event => { if (state.dirty && canAdminister) { event.preventDefault(); event.returnValue = ""; } });
  const definition = definitions.find(item => item.id === definitionId);
  const selectedNode = draft.nodes.find(node => node.id === selected[0]);
  const document = useMemo(() => ({ ...graphToDefinition(draft.nodes, draft.edges, workflowSchema, draft.start), defaultPersonaId: draft.defaultPersonaId }), [draft.nodes, draft.edges, draft.start, draft.defaultPersonaId]);

  async function openVersion(item: WorkflowDefinitionResponse, version?: WorkflowDefinitionVersionResponse) {
    initialized.current = true; const token = ++generation.current; setLoadingDraft(true);
    setFocusNodeId(undefined); setMeasurements({}); setDefinitionId(item.id); setVersionId(version?.id); setSelected([]); setSelectedEdges([]); setNotice(""); setSwitcher(false); setSettings(false);
    const doc = toNodeDefinition(version?.definition ?? createDefaultDefinitionDocument());
    const graph = definitionToGraph(doc);
    try { if (!doc.editor?.positions || Object.keys(doc.editor.positions).length === 0) graph.nodes = await arrange(graph.nodes, graph.edges); }
    catch { onError("Automatic layout failed. You can still move nodes and use Arrange again."); }
    if (token !== generation.current) return;
    state.reset({ name: item.name, version: "", enabled: version?.isEnabled ?? true, isDefault: version?.isDefault ?? false, start: doc.startStepId, defaultPersonaId: doc.defaultPersonaId || undefined, ...graph });
    setLoadingDraft(false);
    window.setTimeout(() => { if (token !== generation.current) return; if (doc.editor?.viewport) void setViewport(doc.editor.viewport); else void fitView({ padding: 0.2 }); }, 50);
  }
  useEffect(() => { if (!initialized.current && definitions.length) void openVersion(definitions[0], definitions[0].versions[0]); }, [definitions]);
  useEffect(() => () => { generation.current++; initialized.current = false; }, []);
  useEffect(() => {
    const token = ++validationGeneration.current;
    if (!canAdminister || loadingDraft) return;
    const timer = window.setTimeout(async () => {
      try { const result = await validateWorkflowDefinition(document); if (token === validationGeneration.current) { setErrors(result.errors); setValidationFailure(""); } }
      catch { if (token === validationGeneration.current) setValidationFailure("Validation is unavailable. Save will validate enabled versions again."); }
    }, 400);
    return () => { window.clearTimeout(timer); validationGeneration.current++; };
  }, [document, canAdminister, loadingDraft]);
  const guard = (action: () => void) => { state.commit(); if (state.dirty) setPending({ message: "Discard unsaved workflow changes?", label: "Discard", action }); else action(); };
  const reveal = (id: string) => { setSelected([id]); setSelectedEdges([]); setInspector(true); setSettings(false); setFocusNodeId(id); };
  useEffect(() => {
    if (!focusNodeId || !nodesInitialized) return;
    const node = getNode(focusNodeId);
    if (!node?.measured?.width || !node.measured.height) return;
    void fitView({ nodes: [{ id: focusNodeId }], padding: 0.6, maxZoom: 1, duration: 180 });
    setFocusNodeId(undefined);
  }, [focusNodeId, nodesInitialized, getNode, fitView]);
  const update = (edit: (current: EditorDraft) => EditorDraft) => { if (editable) state.update(edit); };
  const remove = () => { if (!editable) return; state.commit(); state.update(current => ({ ...current, nodes: current.nodes.filter(node => !selected.includes(node.id)), edges: current.edges.filter(edge => !selectedEdges.includes(edge.id) && !selected.includes(edge.source) && !selected.includes(edge.target)) })); setSelected([]); setSelectedEdges([]); };
  useEffect(() => {
    const handle = (event: KeyboardEvent) => {
      if ((event.target as HTMLElement)?.closest("input,textarea,select,[contenteditable=true],dialog") || !editable) return;
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "z") { event.preventDefault(); event.shiftKey ? state.redo() : state.undo(); }
      else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "y") { event.preventDefault(); state.redo(); }
      else if (event.key === "Delete" || event.key === "Backspace") { event.preventDefault(); remove(); }
    };
    window.addEventListener("keydown", handle); return () => window.removeEventListener("keydown", handle);
  });
  const validConnection = (connection: Connection | Edge) => {
    const source = draft.nodes.find(node => node.id === connection.source), target = draft.nodes.find(node => node.id === connection.target);
    return !!source && !!target && source.id !== target.id && target.data.uses !== triggerUses && !(connection.sourceHandle === "body" && (target.data.uses === loopUses || target.data.uses === parallelUses || target.data.uses === decisionUses)) && (!connection.sourceHandle?.startsWith("branch:") || (target.data.uses === "builtins.plan" && connection.targetHandle !== "join" && connection.targetHandle !== "return")) && (connection.targetHandle !== "join" || (target.data.uses === parallelUses && source.data.uses === "builtins.plan")) && (connection.targetHandle !== "return" || (target.data.uses === loopUses && source.data.uses !== triggerUses && source.data.uses !== loopUses && source.data.uses !== parallelUses && source.data.uses !== decisionUses));
  };
  function connect(source: string, port: string, target?: string, targetPort = "input") {
    if (!editable) return;
    const existing = draft.edges.find(edge => edge.source === source && edge.sourceHandle === port);
    if (target && !validConnection(makeEdge(source, port, target, targetPort))) { setNotice("That connection is not allowed."); return; }
    if (existing?.target === target && existing?.targetHandle === targetPort) return;
    const action = () => { state.commit(); state.update(current => ({ ...current, edges: [...current.edges.filter(edge => !(edge.source === source && edge.sourceHandle === port)), ...(target ? [makeEdge(source, port, target, targetPort)] : [])] })); };
    if (existing && target) setPending({ message: "Replace this output's existing connection?", label: "Replace", action }); else action();
  }
  function canAdd(uses: string) {
    if (!context) return true;
    const existing = draft.edges.find(edge => edge.source === context.source && edge.sourceHandle === context.port);
    if (uses === triggerUses) return false;
    if (context.port.startsWith("branch:") || existing?.targetHandle === "join") return uses === "builtins.plan";
    return !((uses === loopUses || uses === parallelUses || uses === decisionUses) && (context.port === "body" || !!existing));
  }
  function add(uses: string) {
    if (!editable) return;
    const existing = context && draft.edges.find(edge => edge.source === context.source && edge.sourceHandle === context.port);
    if (!canAdd(uses)) return;
    let id = `step${draft.nodes.length + 1}`; let suffix = 2;
    while (draft.nodes.some(node => node.id === id)) id = `step${draft.nodes.length + 1}-${suffix++}`;
    const bounds = canvas.current!.getBoundingClientRect();
    const source = draft.nodes.find(node => node.id === context?.source);
    const node: WorkflowStepNode = { id, type: "workflowStep", position: source ? { x: source.position.x + 350, y: source.position.y + (context?.port === "exit" ? 200 : 0) } : screenToFlowPosition({ x: bounds.x + bounds.width / 2 - 120, y: bounds.y + bounds.height / 2 - 60 }), data: { stepId: id, displayName: catalog.find(item => item.uses === uses)!.title, uses,
      decision: uses === decisionUses ? { condition: { source: "literal", valueType: "string", operator: "equals", value: "", compareTo: "", missingValue: "error" }, trueStepId: "", falseStepId: "" } : undefined,
      parallel: uses === parallelUses ? { branchStepIds: ["", ""] } : undefined,
      loop: uses === loopUses ? { bodyStepId: "", repeatCount: 2, maxIterations: 2 } : undefined,
      trigger: uses === triggerUses ? { type: "DevOpsIssueLabel", enabled: true, repositoryIds: [], label: "" } : undefined } };
    state.commit(); state.update(current => ({ ...current, nodes: [...current.nodes, node], edges: context ? [...current.edges.filter(edge => edge !== existing), makeEdge(context.source, context.port, id), ...(existing ? [makeEdge(id, "next", existing.target, existing.targetHandle || "input")] : [])] : current.edges }));
    setMenu(false); setContext(undefined); reveal(id);
  }
  async function layout() {
    if (!editable) return; state.commit(); setArranging(true); const token = generation.current;
    try { const nodes = await arrange(draft.nodes, draft.edges); if (token === generation.current) { state.update(current => ({ ...current, nodes })); window.setTimeout(() => void fitView({ padding: 0.2 }), 30); } }
    catch { onError("Could not arrange this graph. Existing positions were kept."); }
    finally { setArranging(false); }
  }
  async function save() {
    if (!editable || saving) return;
    if (!draft.name.trim()) { setSettings(true); setNotice("Definition name is required."); return; }
    state.commit(); const submitted = draft; const saveGeneration = generation.current; validationGeneration.current++; setSaving(true); setNotice("");
    try {
      let id = definitionId;
      if (!id) { const created = await createWorkflowDefinition({ name: draft.name.trim() }); id = created.id; if (saveGeneration !== generation.current) return; setDefinitionId(id); }
      const version = await createWorkflowDefinitionVersion(id, { version: draft.version ? Number(draft.version) : null, isEnabled: draft.enabled, isDefault: draft.isDefault, definition: { ...document, editor: { ...document.editor!, viewport: getViewport() } } });
      if (saveGeneration !== generation.current) return;
      setVersionId(version.id); const authoritative = { ...submitted, nodes: submitted.nodes.map(node => ({ ...node, data: { ...node.data, personaSnapshot: version.definition.steps.find(step => step.id === node.id)?.personaSnapshot } })) }; state.saved(submitted, authoritative); setNotice("Workflow definition version saved."); onSaved("Workflow definition version saved.");
      await onRefresh(id, version.id);
    } catch (failure) { if (saveGeneration !== generation.current) return; if (failure instanceof ApiError && failure.validationErrors.length) { setErrors(failure.validationErrors); setProblems(true); } else onError(failure instanceof Error ? failure.message : "Could not save this version."); }
    finally { if (saveGeneration === generation.current) setSaving(false); }
  }
  function newDefinition() { if (saving) return; guard(() => { void (async () => {
    initialized.current = true; const token = ++generation.current; setLoadingDraft(true); setFocusNodeId(undefined); setMeasurements({}); setDefinitionId(undefined); setVersionId(undefined); setSelected([]); setSettings(true); setSwitcher(false); setNotice("");
    let nodes = initial.nodes;
    try { nodes = await arrange(initial.nodes, initial.edges); } catch { onError("Automatic layout failed. Use Arrange to retry."); }
    if (token !== generation.current) return;
    state.reset({ ...initial, nodes }); setLoadingDraft(false); window.setTimeout(() => void fitView({ padding: 0.2 }), 30);
  })(); }); }
  const selectedSet = new Set(selected), edgeSet = new Set(selectedEdges);
  const contextualEdge = context && draft.edges.find(edge => edge.source === context.source && edge.sourceHandle === context.port);
  return <section className="workflow-editor" aria-label="Workflow editor">
    <header className="editor-header">
      <button type="button" className="editor-workflow-name" disabled={saving} onClick={() => setSwitcher(!switcher)}>{draft.name} ▾</button>
      <label className="editor-version"><span className="sr-only">Workflow version</span><select aria-label="Workflow version" value={versionId || ""} disabled={!definition || loadingDraft || saving} onChange={event => { const version = definition?.versions.find(item => item.id === event.target.value); if (definition && version) guard(() => void openVersion(definition, version)); }}><option value="" disabled>New workflow</option>{definition?.versions.map(version => <option key={version.id} value={version.id}>v{version.version}{version.isEnabled ? " · Enabled" : " · Disabled"}</option>)}</select></label>
      <span role="status" className="editor-save-status">{loadingDraft ? "Loading…" : saving ? "Saving…" : state.dirty ? "Unsaved changes" : versionId ? "Saved" : "Draft"}</span>
      <button type="button" onClick={() => { void onRefresh(); void refreshPersonas(); }} disabled={saving || loading}>Refresh</button>
      <button type="button" onClick={() => setSettings(!settings)}>Workflow settings</button>
      <button type="button" className="primary-button" disabled={!editable || saving} onClick={() => void save()}>Save Version</button>
    </header>
    <div className="editor-toolbar" role="group" aria-label="Canvas commands">
      <div className="editor-tool-group" role="group" aria-label="Edit workflow">
      <button type="button" className="editor-add-step" disabled={!editable} onClick={() => { setContext(undefined); setQuery(""); setMenu(!menu); }}>+ Add Step</button>
      <button type="button" disabled={!editable || !state.canUndo} onClick={state.undo} className="editor-icon-button" aria-label="Undo" title="Undo (Ctrl+Z)"><ToolbarIcon name="undo" /></button><button type="button" disabled={!editable || !state.canRedo} onClick={state.redo} className="editor-icon-button" aria-label="Redo" title="Redo (Ctrl+Shift+Z)"><ToolbarIcon name="redo" /></button>
      <button type="button" disabled={!editable || (!selected.length && !selectedEdges.length)} onClick={remove} className="editor-icon-button" aria-label="Delete" title="Delete selection"><ToolbarIcon name="delete" /></button>
      <button type="button" disabled={!editable || !selectedNode || selectedNode.data.uses === loopUses || selectedNode.data.uses === triggerUses || selectedNode.data.uses === parallelUses || selectedNode.data.uses === decisionUses} onClick={() => { const original = selectedNode!; let id = `${original.id}-copy`; let suffix = 2; while (draft.nodes.some(node => node.id === id)) id = `${original.id}-copy-${suffix++}`; state.update(current => ({ ...current, nodes: [...current.nodes, { ...original, id, position: { x: original.position.x + 40, y: original.position.y + 160 }, data: { ...original.data, stepId: id, displayName: `${original.data.displayName} copy` } }] })); reveal(id); }} className="editor-icon-button" aria-label="Duplicate task" title="Duplicate task"><ToolbarIcon name="duplicate" /></button>
      </div>
      <div className="editor-tool-group" role="group" aria-label="Canvas view">
      <button type="button" disabled={!editable} onClick={() => void layout()}><ToolbarIcon name="arrange" />{arranging ? "Arranging…" : "Arrange"}</button>
      <button type="button" onClick={() => void fitView({ padding: 0.2 })} title="Fit all nodes"><ToolbarIcon name="fit" />Fit All</button><button type="button" disabled={!selected.length} onClick={() => void fitView({ nodes: selected.map(id => ({ id })), padding: 0.4, maxZoom: 1 })} className="editor-icon-button" aria-label="Fit Selection" title="Fit selection"><ToolbarIcon name="selection" /></button>
      <span className="editor-zoom" aria-label="Zoom percentage">{zoom}%</span><button type="button" aria-pressed={miniMap} onClick={() => setMiniMap(!miniMap)} className="editor-icon-button" aria-label="Minimap" title="Toggle minimap"><ToolbarIcon name="map" /></button>
      </div>
      <div className="editor-tool-group editor-tools-end">
      <label className="editor-search-field"><ToolbarIcon name="search" /><input className="editor-node-search" aria-label="Find a node" placeholder="Find a node…" value={nodeQuery} onChange={event => setNodeQuery(event.target.value)} /></label>
      <button type="button" className={`editor-problems-toggle${errors.length ? " has-problems" : ""}`} aria-expanded={problems} onClick={() => setProblems(!problems)}><ToolbarIcon name="problems" />Problems ({errors.length})</button>
      </div>
    </div>
    {(error || notice || validationFailure || personaError) && <div className="editor-message" role="status">{error || notice || validationFailure || personaError}</div>}
    <div className="editor-body">
      <div className="editor-canvas" ref={canvas}>
        <NodeActions.Provider value={{ start: draft.start, errors: new Set(errors.flatMap(error => error.nodeId ? [error.nodeId] : [])), editable, add: (source, port) => { setContext({ source, port }); setQuery(""); setMenu(true); } }}>
          <ReactFlow nodes={draft.nodes.map(node => ({ ...node, measured: measurements[node.id], selected: selectedSet.has(node.id) }))} edges={draft.edges.map(edge => ({ ...edge, selected: edgeSet.has(edge.id) }))} nodeTypes={nodeTypes} minZoom={0.02} maxZoom={2} nodesDraggable={editable} nodesConnectable={editable} edgesReconnectable={editable} deleteKeyCode={null} onNodeDragStart={state.begin} onNodeDragStop={state.commit}
            onNodesChange={changes => {
              const dimensions = changes.filter(change => change.type === "dimensions" && change.dimensions);
              if (dimensions.length) setMeasurements(current => {
                let next = current;
                for (const change of dimensions) {
                  if (change.type !== "dimensions" || !change.dimensions) continue;
                  const previous = current[change.id];
                  if (previous?.width === change.dimensions.width && previous?.height === change.dimensions.height) continue;
                  if (next === current) next = { ...current };
                  next[change.id] = change.dimensions;
                }
                return next;
              });
              const selection = changes.filter(change => change.type === "select"); if (selection.length) setSelected(current => { const ids = new Set(current); selection.forEach(change => { if (change.type === "select") change.selected ? ids.add(change.id) : ids.delete(change.id); }); return [...ids]; }); if (editable && changes.some(change => change.type === "position" && change.position)) update(current => ({ ...current, nodes: current.nodes.map(node => { const change = changes.find(change => change.type === "position" && change.id === node.id); return change?.type === "position" && change.position ? { ...node, position: change.position } : node; }) })); }}
            onEdgesChange={changes => setSelectedEdges(current => { const ids = new Set(current); changes.forEach(change => { if (change.type === "select") change.selected ? ids.add(change.id) : ids.delete(change.id); }); return [...ids]; })}
            onNodeClick={(_, node) => { setInspector(true); setSettings(false); }} onPaneClick={() => { setMenu(false); }}
            onConnect={connection => connect(connection.source, connection.sourceHandle || "next", connection.target, connection.targetHandle || "input")} isValidConnection={validConnection}
            onReconnect={(old, connection) => { if (old.source !== connection.source || old.sourceHandle !== connection.sourceHandle) { setNotice("Reconnect the target, or disconnect and choose a new output in the inspector."); return; } connect(connection.source, connection.sourceHandle || "next", connection.target, connection.targetHandle || "input"); }}>
            <Background /> <Controls showInteractive={false} /> {miniMap && <MiniMap pannable zoomable />}
          </ReactFlow>
        </NodeActions.Provider>
        {nodeQuery && <div className="editor-search-results">{draft.nodes.filter(node => `${node.data.displayName} ${node.id}`.toLowerCase().includes(nodeQuery.toLowerCase())).map(node => <button type="button" key={node.id} onClick={() => { reveal(node.id); setNodeQuery(""); }}>{node.data.displayName} ({node.id})</button>)}</div>}
      </div>
      {settings ? <aside className="editor-inspector" aria-label="Workflow settings" onFocusCapture={event => { if (event.target.matches("input,select")) state.begin(); }} onBlurCapture={state.commit}>
        <div className="editor-panel-heading"><h3>Workflow settings</h3><button type="button" aria-label="Close workflow settings" onClick={() => setSettings(false)}>×</button></div>
        <label><span>Definition Name</span><input disabled={!editable || !!definitionId} value={draft.name} onChange={event => update(current => ({ ...current, name: event.target.value }))} required /></label>
        <PersonaPicker label="Workflow persona" value={draft.defaultPersonaId} savedSnapshot={state.savedDraft.nodes.find(node => !node.data.personaId && node.data.personaSnapshot?.id === (draft.defaultPersonaId || "default"))?.data.personaSnapshot} personas={personas} disabled={!editable} onChange={defaultPersonaId => update(current => ({ ...current, defaultPersonaId }))} />
        <label><span>Start Step</span><select value={draft.start} disabled={!editable} onChange={event => update(current => ({ ...current, start: event.target.value }))}><option value="">Choose a start</option>{draft.nodes.filter(node => node.data.uses !== triggerUses).map(node => <option key={node.id} value={node.id}>{node.data.displayName}</option>)}</select></label>
        <label className="toggle-label"><input type="checkbox" checked={draft.enabled} disabled={!editable} onChange={event => update(current => ({ ...current, enabled: event.target.checked }))} /><span>Enabled</span></label>
        <p className="muted">Enabled versions can start workflows. Disabled versions may be saved with incomplete steps.</p>
        <label className="toggle-label"><input type="checkbox" checked={draft.isDefault} disabled={!editable} onChange={event => update(current => ({ ...current, isDefault: event.target.checked }))} /><span>Default</span></label><p className="muted">Saving a default enabled version changes the default for new runs. Existing versions and runs remain intact.</p>
        <details className="optional-settings"><summary>Advanced</summary><label><span>Schema</span><input readOnly value={workflowSchema} /></label><label><span>Version number</span><input type="number" min="1" disabled={!editable} placeholder="Automatic" value={draft.version} onChange={event => update(current => ({ ...current, version: event.target.value }))} /></label></details>
      </aside> : inspector && selectedNode ? <Inspector key={selectedNode.id} savedPersonaSnapshot={state.savedDraft.nodes.find(node => node.id === selectedNode.id)?.data.personaSnapshot} personas={personas} defaultPersonaId={draft.defaultPersonaId} node={selectedNode} nodes={draft.nodes} edges={draft.edges} disabled={!editable} errors={errors.filter(error => error.nodeId === selectedNode.id)} begin={state.begin} commit={state.commit} close={() => setInspector(false)}
        update={values => update(current => ({ ...current, nodes: current.nodes.map(node => node.id === selectedNode.id ? { ...node, data: { ...node.data, ...values } } : node) }))}
        resizeBranches={count => {
          if (count < 2 || count > 8) return;
          state.commit(); update(current => ({ ...current,
            nodes: current.nodes.map(node => node.id === selectedNode.id ? { ...node, data: { ...node.data, parallel: { branchStepIds: Array.from({ length: count }, (_, index) => node.data.parallel?.branchStepIds[index] ?? "") } } } : node),
            edges: current.edges.filter(edge => edge.source !== selectedNode.id || !edge.sourceHandle?.startsWith("branch:") || Number(edge.sourceHandle.slice(7)) < count)
          }));
        }}
        move={(axis, value) => { if (Number.isFinite(value)) update(current => ({ ...current, nodes: current.nodes.map(node => node.id === selectedNode.id ? { ...node, position: { ...node.position, [axis]: value } } : node) })); }}
        rename={id => { if (!editable || id === selectedNode.id) return; if (!id || draft.nodes.some(node => node.id === id)) { setNotice("Step IDs must be nonempty and unique."); return; } update(current => ({ ...current, start: current.start === selectedNode.id ? id : current.start, nodes: current.nodes.map(node => node.id === selectedNode.id ? { ...node, id, data: { ...node.data, stepId: id } } : node.data.decision?.condition.source === "taskOutput" && node.data.decision.condition.reference === selectedNode.id ? { ...node, data: { ...node.data, decision: { ...node.data.decision, condition: { ...node.data.decision.condition, reference: id } } } } : node), edges: current.edges.map(edge => makeEdge(edge.source === selectedNode.id ? id : edge.source, edge.sourceHandle || "next", edge.target === selectedNode.id ? id : edge.target, edge.targetHandle || "input")) })); setSelected([id]); }}
        start={() => update(current => ({ ...current, start: selectedNode.id }))} connect={(port, target, targetPort) => connect(selectedNode.id, port, target, targetPort)} /> : null}
      {menu && <aside className="editor-popover editor-catalog" aria-label="Add step menu"><div className="editor-panel-heading"><h3>{context ? contextualEdge ? "Insert task" : "Add connected step" : "Add Step"}</h3><button type="button" aria-label="Close add menu" onClick={() => setMenu(false)}>×</button></div><input autoFocus aria-label="Search step types" placeholder="Search steps…" value={query} onChange={event => setQuery(event.target.value)} />{contextualEdge && <p>The new task will keep the existing downstream connection.</p>}{catalog.filter(item => `${item.title} ${item.description}`.toLowerCase().includes(query.toLowerCase())).map(item => <button type="button" key={item.uses} disabled={!editable || !canAdd(item.uses)} onClick={() => add(item.uses)}><strong><StepIcon uses={item.uses} /> {item.title}</strong><span>{item.description}</span></button>)}</aside>}
      {switcher && <aside className="editor-popover editor-switcher" aria-label="Choose workflow"><div className="editor-panel-heading"><h3>Workflows</h3><button type="button" aria-label="Close workflow switcher" onClick={() => setSwitcher(false)}>×</button></div><input autoFocus aria-label="Search workflows" value={workflowQuery} onChange={event => setWorkflowQuery(event.target.value)} /><button type="button" disabled={!editable || saving} onClick={newDefinition}>New Definition</button>{definitions.filter(item => item.name.toLowerCase().includes(workflowQuery.toLowerCase())).map(item => <button type="button" key={item.id} disabled={saving || loadingDraft} onClick={() => { if (!saving) guard(() => void openVersion(item, item.versions[0])); }}>{item.name}</button>)}</aside>}
    </div>
    {problems && <section className="editor-problems" aria-label="Workflow problems"><div className="editor-panel-heading"><h3>Problems ({errors.length})</h3><button type="button" onClick={() => setProblems(false)}>Close</button></div>{errors.length === 0 && <p>No problems found.</p>}{errors.map((error, index) => <button type="button" key={index} onClick={() => { if (error.nodeId) { reveal(error.nodeId); setSettings(false); } else setSettings(true); }}>{error.message}</button>)}</section>}
    {(pending || blocker.state === "blocked") && <Confirm message={pending?.message || "Discard unsaved workflow changes and leave?"} label={pending?.label || "Discard"} cancel={() => { setPending(undefined); if (blocker.state === "blocked") blocker.reset(); }} confirm={() => { if (pending) { const action = pending.action; setPending(undefined); action(); } else if (blocker.state === "blocked") blocker.proceed(); }} />}
  </section>;
}
function Confirm({ message, label, cancel, confirm }: { message: string; label: string; cancel: () => void; confirm: () => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => { ref.current?.showModal(); }, []);
  const titleId = useId(), descriptionId = useId();
  const replacing = label === "Replace";
  return <dialog ref={ref} className="editor-confirm" onCancel={event => { event.preventDefault(); cancel(); }} aria-labelledby={titleId} aria-describedby={descriptionId}>
    <div className="editor-confirm-content"><span className="editor-confirm-kicker">{replacing ? "Connection change" : "Unsaved changes"}</span>
      <h2 id={titleId}>{replacing ? "Replace connection?" : "Discard your changes?"}</h2>
      <p id={descriptionId}>{replacing ? "This output is already connected. Replacing it will disconnect the current target and connect the selected step." : `${message} Your edits since the last save will be lost.`}</p>
    </div>
    <div className="editor-confirm-actions"><button type="button" autoFocus onClick={cancel}>{replacing ? "Cancel" : "Stay"}</button><button type="button" className={`primary-button${replacing ? "" : " editor-discard-button"}`} onClick={confirm}>{label}</button></div>
  </dialog>;
}
