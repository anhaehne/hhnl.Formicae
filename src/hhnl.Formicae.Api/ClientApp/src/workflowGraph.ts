import { variableUses, issueCommentUses, waitUses } from "./workflowData";
import type { WorkflowEditorGroup, WorkflowDataVariable, WorkflowIssueCommentSettings, ImageSelection, PreparedImageSnapshot } from "./api";
import { isEventUses, adaptEventStep } from "./workflowEvents";
import { MarkerType, type Edge, type Node } from "@xyflow/react";
import type { WorkflowDefinitionDocument, WorkflowDefinitionResponse, WorkflowDefinitionVersionResponse, WorkflowTriggerNodeSettings, WorkflowEventSettings, WorkflowLoopNodeSettings, WorkflowParallelNodeSettings, WorkflowDecisionNodeSettings, PersonaSnapshot, WorkflowCustomTaskSettings, EnvironmentSnapshot, WorkflowScriptSettings, WorkflowWaitSettings, StepSecretReference } from "./api";

export { waitUses, waitOutputs, waitSchema } from "./workflowData";
export const scriptUses = "builtins.script";
export const agentTaskUses = "builtins.agent-task";
export const customTaskUses = "builtins.custom-task";
export const triggerUses = "builtins.trigger";
export const startUses = "builtins.start";
export const isStartUses = isEventUses;
export const decisionUses = "builtins.decision";
export const parallelUses = "builtins.parallel";
export const loopUses = "builtins.loop";
export const workflowSchema = "formicae.workflow/v1alpha3";
export const supportedUses = ["builtins.plan", "builtins.implement", "builtins.create-pull-request", "builtins.address-comments", customTaskUses, agentTaskUses, scriptUses, issueCommentUses, waitUses] as const;
export type WorkflowStepNodeData = {
  variable?: WorkflowDataVariable;
  stepId: string; displayName: string; uses: string; aiSettingsId?: string | null; model?: string | null;
  imageSelection?: ImageSelection | null; imageSnapshot?: PreparedImageSnapshot | null;
  personaId?: string | null; personaSnapshot?: PersonaSnapshot | null; environmentId?: string | null; environmentSnapshot?: EnvironmentSnapshot | null; customTask?: WorkflowCustomTaskSettings | null;
  wait?: WorkflowWaitSettings | null;
  issueComment?: WorkflowIssueCommentSettings | null; script?: WorkflowScriptSettings | null; capabilities?: string[] | null; secretReferences?: StepSecretReference[] | null;
  event?: WorkflowEventSettings | null; trigger?: WorkflowTriggerNodeSettings | null; loop?: WorkflowLoopNodeSettings | null; parallel?: WorkflowParallelNodeSettings | null; decision?: WorkflowDecisionNodeSettings | null;
  [key: string]: unknown;
};
export type WorkflowStepNode = Node<WorkflowStepNodeData, "workflowStep">;

export function createDefaultDefinitionDocument(): WorkflowDefinitionDocument {
  return { schema: workflowSchema, startStepId: "manual-start", steps: [
    { id: "manual-start", uses: startUses, displayName: "Manual start", nextStepId: "plan", event: { enabled: true } },
    { id: "plan", uses: "builtins.plan", nextStepId: "implement", displayName: "Plan" },
    { id: "implement", uses: "builtins.implement", nextStepId: "createPullRequest", displayName: "Implement" },
    { id: "createPullRequest", uses: "builtins.create-pull-request", nextStepId: "addressComments", displayName: "Create pull request" },
    { id: "addressComments", uses: "builtins.address-comments", displayName: "Address comments" }
  ] };
}

// Convert only an editor draft; the original persisted version is never rewritten.
function toLegacyNodeDefinition(document: WorkflowDefinitionDocument): WorkflowDefinitionDocument {
  if (document.schema === workflowSchema) return document;
  const ids = new Set(document.steps.map(step => step.id));
  const allocate = (prefix: string) => {
    let id = prefix; let suffix = 2;
    while (ids.has(id)) id = `${prefix}-${suffix++}`;
    ids.add(id); return id;
  };
  const loops = (document.loops ?? []).map(loop => ({ ...loop, nodeId: allocate(`loop-${loop.id}`) }));
  const entry = (id?: string | null) => loops.find(loop => loop.bodyStepIds[0] === id)?.nodeId ?? id;
  const steps = document.steps.map(step => {
    const returning = loops.find(loop => loop.bodyStepIds.at(-1) === step.id);
    return { ...step, nextStepId: returning?.nodeId ?? entry(step.nextStepId), nextStepPort: returning ? "return" as const : null };
  });
  for (const loop of loops) steps.push({ id: loop.nodeId, uses: loopUses, displayName: loop.id,
    nextStepId: entry(loop.exitStepId), nextStepPort: null,
    loop: { bodyStepId: loop.bodyStepIds[0] ?? "", repeatCount: loop.repeatCount, maxIterations: loop.maxIterations, timeoutSeconds: loop.timeoutSeconds } });
  for (const trigger of document.triggers ?? []) {
    const { id, ...settings } = trigger;
    steps.push({ id: allocate(`trigger-${id}`), uses: triggerUses, displayName: id,
      trigger: settings, nextStepId: entry(document.startStepId), nextStepPort: null });
  }
  return { schema: workflowSchema, variables: document.variables, defaultEnvironmentId: document.defaultEnvironmentId, defaultEnvironmentSnapshot: document.defaultEnvironmentSnapshot, defaultPersonaId: document.defaultPersonaId, startStepId: entry(document.startStepId)!, steps };
}

export function toNodeDefinition(original: WorkflowDefinitionDocument): WorkflowDefinitionDocument {
  let document = toLegacyNodeDefinition(original);
  const hadEvents = document.steps.some(step => step.uses === startUses || step.event);
  document = { ...document, steps: document.steps.map(adaptEventStep) };
  if (hadEvents) return document;
  const steps = document.steps.map(step => step.uses === triggerUses ? { ...step, uses: startUses } : step);
  let id = "manual-start", suffix = 2;
  while (steps.some(step => step.id === id)) id = `manual-start-${suffix++}`;
  const target = document.editor?.positions[document.startStepId];
  steps.push({ id, uses: startUses, displayName: "Manual start", nextStepId: document.startStepId,
    event: { enabled: true } });
  return { ...document, startStepId: id, steps, editor: document.editor ? { ...document.editor,
    positions: { ...document.editor.positions, [id]: { x: (target?.x ?? 0) - 350, y: target?.y ?? 80 } } } : undefined };
}

export function definitionToGraph(original: WorkflowDefinitionDocument, adaptStarts = true): { nodes: WorkflowStepNode[]; edges: Edge[]; groups: WorkflowEditorGroup[] } {
  const document = adaptStarts ? toNodeDefinition(original) : toLegacyNodeDefinition(original);
  const nodes: WorkflowStepNode[] = document.steps.map((step, index) => ({
    id: step.id, type: "workflowStep", position: document.editor?.positions[step.id] ?? { x: (index % 3) * 280, y: Math.floor(index / 3) * 200 + 80 },
    data: { stepId: step.id, displayName: step.displayName || step.id, uses: step.uses,
      aiSettingsId: step.aiSettingsId, model: step.model, personaId: step.personaId, personaSnapshot: step.personaSnapshot, imageSelection: step.imageSelection, imageSnapshot: step.imageSnapshot, environmentId: step.environmentId, environmentSnapshot: step.environmentSnapshot, customTask: step.customTask, wait: step.wait, event: step.event, issueComment: step.issueComment, script: step.script, capabilities: step.capabilities, secretReferences: step.secretReferences, trigger: step.trigger, loop: step.loop, parallel: step.parallel, decision: step.decision }
  }));
  for (const variable of document.variables ?? []) nodes.push({ id: variable.id, type: "workflowStep", position: document.editor?.positions[variable.id] ?? { x: 100, y: 100 + nodes.length * 100 }, data: { stepId: variable.id, displayName: variable.name, uses: variableUses, variable } });
  const edges: Edge[] = [];
  for (const variable of document.variables ?? []) for (const source of variable.sources ?? []) edges.push(dataEdge(source.stepId, source.outputName, variable.id, "value"));
  for (const step of document.steps) {
    if (step.wait?.issueNumberBinding) edges.push(dataEdge(step.wait.issueNumberBinding.stepId, step.wait.issueNumberBinding.outputName, step.id, "issueNumber"));
    for (const [name, binding] of Object.entries(step.issueComment?.bindings ?? step.customTask?.bindings ?? {})) edges.push(dataEdge(binding.stepId, binding.outputName, step.id, name));
    for (const target of [step.nextStepId, ...(step.nextStepIds ?? [])].filter((id): id is string => !!id)) edges.push({ id: `${step.id}:next:${target}`, source: step.id, target,
      markerEnd: { type: MarkerType.ArrowClosed }, style: step.nextStepPort === "join" ? { strokeDasharray: "3 3", stroke: "#62509b" } : step.nextStepPort === "return" ? { strokeDasharray: "6 4", stroke: "#986c26" } : undefined,
      sourceHandle: step.uses === loopUses ? "exit" : "next", targetHandle: step.nextStepPort || "input",
      label: step.nextStepPort === "join" ? "Join" : step.nextStepPort === "return" ? "Return" : step.uses === loopUses ? "Exit" : undefined });
    if (step.decision) for (const [port, target] of [["true", step.decision.trueStepId], ["false", step.decision.falseStepId]]) {
      if (target) edges.push({ id: `${step.id}:${port}`, source: step.id, sourceHandle: port, target, targetHandle: "input", markerEnd: { type: MarkerType.ArrowClosed }, label: port === "true" ? "True" : "False" });
    }
    step.parallel?.branchStepIds.forEach((target, index) => {
      if (target) edges.push({ id: `${step.id}:branch:${index}`, source: step.id, sourceHandle: `branch:${index}`, target, targetHandle: "input", markerEnd: { type: MarkerType.ArrowClosed }, label: `Branch ${index + 1}` });
    });
    if (step.loop?.bodyStepId) edges.push({ id: `${step.id}:body`, source: step.id, sourceHandle: "body",
      markerEnd: { type: MarkerType.ArrowClosed }, target: step.loop.bodyStepId, targetHandle: "input", label: "Body" });
  }
  return { nodes, edges, groups: document.editor?.groups ?? [] };
}

export function graphToDefinition(nodes: WorkflowStepNode[], edges: Edge[], _schema: string, startStepId: string, groups: WorkflowEditorGroup[] = []): WorkflowDefinitionDocument {
  return { schema: workflowSchema, startStepId: nodes.some(node => isStartUses(node.data.uses)) ? nodes.find(node => node.data.uses === startUses && (node.data.event || node.data.trigger?.type === "Manual"))?.id ?? "" : startStepId, editor: { groups, positions: Object.fromEntries(nodes.map(node => [node.id, node.position])) }, variables: nodes.filter(node => node.data.variable).map(node => ({ ...node.data.variable!, id: node.id, name: node.data.displayName, sources: edges.filter(edge => isDataEdge(edge) && edge.target === node.id).map(edge => ({ stepId: edge.source, outputName: edge.sourceHandle!.slice(7) })) })), steps: nodes.filter(node => !node.data.variable).map(node => {
    const next = edges.find(edge => edge.source === node.id && (edge.sourceHandle === "next" || edge.sourceHandle === "exit" || !edge.sourceHandle));
    const additional = edges.filter(edge => edge.source === node.id && edge !== next && (edge.sourceHandle === "next" || !edge.sourceHandle)).map(edge => edge.target);
    const body = edges.find(edge => edge.source === node.id && edge.sourceHandle === "body");
    const bindings = Object.fromEntries(edges.filter(edge => isDataEdge(edge) && edge.target === node.id).map(edge => [edge.targetHandle!.slice(5), { stepId: edge.source, outputName: edge.sourceHandle!.slice(7) }]));
    const customTask = node.data.customTask ? { ...node.data.customTask, taskId: node.data.uses === agentTaskUses ? `agent:${node.id}` : node.data.customTask.taskId, bindings, inputs: Object.fromEntries(Object.entries(node.data.customTask.inputs ?? {}).filter(([name]) => !bindings[name])) } : undefined;
    return { id: node.data.stepId || node.id, uses: node.data.uses, displayName: node.data.displayName,
      nextStepId: node.data.uses === decisionUses ? undefined : next?.target ?? null, nextStepPort: next?.targetHandle === "return" ? "return" : next?.targetHandle === "join" ? "join" : null,
      nextStepIds: additional.length ? additional : undefined,
      personaId: node.data.uses === scriptUses ? undefined : node.data.personaId || undefined, personaSnapshot: node.data.uses === scriptUses ? undefined : node.data.personaSnapshot, imageSelection: node.data.imageSelection, imageSnapshot: node.data.imageSnapshot, environmentId: node.data.environmentId, environmentSnapshot: node.data.environmentSnapshot, customTask: [customTaskUses, agentTaskUses].includes(node.data.uses) ? customTask : undefined,
      aiSettingsId: node.data.uses === scriptUses ? undefined : node.data.aiSettingsId || undefined, model: node.data.uses === scriptUses ? undefined : node.data.model || undefined,
      issueComment: node.data.uses === issueCommentUses ? { ...node.data.issueComment, bindings, inputs: Object.fromEntries(Object.entries(node.data.issueComment?.inputs ?? {}).filter(([name]) => !bindings[name])) } : undefined,
      wait: node.data.uses === waitUses ? { ...node.data.wait, issueNumber: bindings.issueNumber ? undefined : node.data.wait?.issueNumber, issueNumberBinding: bindings.issueNumber } : undefined,
      script: node.data.uses === scriptUses ? node.data.script : undefined, capabilities: node.data.capabilities, secretReferences: node.data.secretReferences,
      decision: node.data.uses === decisionUses && node.data.decision ? { ...node.data.decision,
        trueStepId: edges.find(edge => edge.source === node.id && edge.sourceHandle === "true")?.target ?? "",
        falseStepId: edges.find(edge => edge.source === node.id && edge.sourceHandle === "false")?.target ?? "" } : undefined,
      parallel: node.data.uses === parallelUses ? { branchStepIds: (node.data.parallel?.branchStepIds ?? ["", ""]).map((_, index) => edges.find(edge => edge.source === node.id && edge.sourceHandle === `branch:${index}`)?.target ?? "") } : undefined,
      event: isStartUses(node.data.uses) ? node.data.event : undefined,
      trigger: isStartUses(node.data.uses) && !node.data.event ? node.data.trigger : undefined,
      loop: node.data.uses === loopUses && node.data.loop ? { ...node.data.loop, bodyStepId: body?.target ?? "" } : undefined };
  }) };
}

export function getEnabledDefinitionVersions(definitions: WorkflowDefinitionResponse[]) {
  const versions: Array<{ definition: WorkflowDefinitionResponse; version: WorkflowDefinitionVersionResponse }> = [];
  for (const definition of definitions) {
    for (const version of definition.versions) {
      if (version.isEnabled && (!version.definition.steps.some(step => step.event || step.uses === startUses) || version.definition.steps.some(step => step.uses === startUses && (step.event?.enabled || step.trigger?.type === "Manual" && step.trigger.enabled)))) {
        versions.push({ definition, version });
      }
    }
  }

  return versions.sort((left, right) => {
    if (left.version.isDefault !== right.version.isDefault) {
      return left.version.isDefault ? -1 : 1;
    }

    return right.version.version - left.version.version;
  });
}

export const isDataEdge = (edge: Edge) => !!edge.sourceHandle?.startsWith("output:");
export const dataEdge = (source: string, output: string, target: string, input: string): Edge => ({ id: `data:${source}:${output}:${target}:${input}`, source, target, sourceHandle: `output:${output}`, targetHandle: `data:${input}`, label: `${output} → ${input}`, style: { stroke: "#168b85", strokeDasharray: "4 3" }, markerEnd: { type: MarkerType.ArrowClosed } });

// Data connections never participate in control traversal or layout.
export function eligibleProducer(nodes: WorkflowStepNode[], edges: Edge[], start: string, producer: string, consumer: string) {
  const producerNode = nodes.find(node => node.id === producer);
  if (producerNode?.data.variable) {
    const seen = new Set<string>();
    const check = (id: string): boolean => {
      if (seen.has(id)) return false;
      const variable = nodes.find(node => node.id === id)?.data.variable;
      if (!variable) return eligibleProducer(nodes, edges, start, id, consumer);
      seen.add(id);
      const valid = edges.filter(edge => isDataEdge(edge) && edge.target === id).every(edge => check(edge.source));
      seen.delete(id); return valid;
    };
    return check(producer);
  }
  const control = edges.filter(edge => !isDataEdge(edge));
  const path = (from: string, to: string) => {
    const seen = new Set<string>(), pending = control.filter(edge => edge.source === from).map(edge => edge.target);
    while (pending.length) { const id = pending.pop()!; if (id === to) return true; if (seen.has(id)) continue; seen.add(id); pending.push(...control.filter(edge => edge.source === id).map(edge => edge.target)); }
    return false;
  };
  if (path(producer, consumer) && path(consumer, producer)) return true;
  const loops = new Map<string, string>();
  for (const loop of nodes.filter(node => node.data.uses === loopUses)) {
    let cursor = control.find(edge => edge.source === loop.id && edge.sourceHandle === "body")?.target;
    const seen = new Set<string>();
    while (cursor && cursor !== loop.id && !seen.has(cursor)) { seen.add(cursor); loops.set(cursor, loop.id); cursor = control.find(edge => edge.source === cursor)?.target; }
  }
  const next = (id: string) => {
    const node = nodes.find(node => node.id === id);
    if (node?.data.uses === loopUses) return control.filter(edge => edge.source === id && edge.sourceHandle === "body").map(edge => edge.target);
    return control.filter(edge => edge.source === id).flatMap(edge => edge.targetHandle === "return" ? control.filter(exit => exit.source === edge.target && exit.sourceHandle === "exit").map(exit => exit.target) : [edge.target]);
  };
  const reach = (entry: string, blocked?: string, target = consumer) => {
    const visited = new Set<string>(), pending = [entry];
    while (pending.length) { const id = pending.pop()!; if (id === blocked || visited.has(id)) continue; if (id === target) return true; visited.add(id); pending.push(...next(id)); }
    return false;
  };
  const entries = [start, ...nodes.filter(node => isStartUses(node.data.uses)).map(node => node.id)].filter(Boolean);
  if (nodes.find(node => node.id === producer)?.data.uses === "github.issue-created") return reach(producer) && !entries.some(entry => entry !== producer && reach(entry));
  const graph = nodes.some(node => node.data.uses !== parallelUses && control.filter(edge => edge.source === node.id && edge.sourceHandle === "next").length > 1);
  return producer !== consumer && (!loops.has(producer) || loops.get(producer) === loops.get(consumer)) && reach(producer) && (graph ? !entries.some(entry => reach(entry) && !reach(entry, undefined, producer)) : !entries.some(entry => reach(entry, producer)));
}

// A variable has no control position; validate cycles immediately and ancestry at its consumers.
export function validVariableSource(nodes: WorkflowStepNode[], edges: Edge[], start: string, producer: string, variable: string): boolean {
  if (producer === variable) return false;
  const pending = [variable], seen = new Set<string>();
  while (pending.length) {
    const id = pending.pop()!;
    if (seen.has(id)) continue;
    seen.add(id);
    for (const edge of edges.filter(edge => isDataEdge(edge) && edge.source === id)) {
      if (edge.target === producer && nodes.find(node => node.id === producer)?.data.variable) return false;
      if (nodes.find(node => node.id === edge.target)?.data.variable) pending.push(edge.target);
      // Adding a variable source is independent of its consumer control position.
      // Saved-definition validation reports unavailable consumer paths.
    }
  }
  return true;
}

export function hasControlCycles(nodes: WorkflowStepNode[], edges: Edge[]): boolean {
  const visited = new Set<string>(), visiting = new Set<string>();
  const next = edges.filter(edge => !isDataEdge(edge) && edge.targetHandle !== "return" && edge.targetHandle !== "join");
  const visit = (id: string): boolean => {
    if (visiting.has(id)) return true;
    if (visited.has(id)) return false;
    visited.add(id); visiting.add(id);
    const cyclic = next.filter(edge => edge.source === id).some(edge => visit(edge.target));
    visiting.delete(id); return cyclic;
  };
  return nodes.some(node => visit(node.id));
}
