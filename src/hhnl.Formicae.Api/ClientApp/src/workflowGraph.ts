import { MarkerType, type Edge, type Node } from "@xyflow/react";
import type { WorkflowDefinitionDocument, WorkflowDefinitionResponse, WorkflowDefinitionVersionResponse, WorkflowTriggerNodeSettings, WorkflowLoopNodeSettings } from "./api";

export const triggerUses = "builtins.trigger";
export const loopUses = "builtins.loop";
export const workflowSchema = "formicae.workflow/v1alpha3";
export const supportedUses = ["builtins.plan", "builtins.implement", "builtins.create-pull-request", "builtins.address-comments"] as const;
export type WorkflowStepNodeData = {
  stepId: string; displayName: string; uses: string; aiSettingsId?: string | null; model?: string | null;
  trigger?: WorkflowTriggerNodeSettings | null; loop?: WorkflowLoopNodeSettings | null;
  [key: string]: unknown;
};
export type WorkflowStepNode = Node<WorkflowStepNodeData, "workflowStep">;

export function createDefaultDefinitionDocument(): WorkflowDefinitionDocument {
  return { schema: workflowSchema, startStepId: "plan", steps: [
    { id: "plan", uses: "builtins.plan", nextStepId: "implement", displayName: "Plan" },
    { id: "implement", uses: "builtins.implement", nextStepId: "createPullRequest", displayName: "Implement" },
    { id: "createPullRequest", uses: "builtins.create-pull-request", nextStepId: "addressComments", displayName: "Create pull request" },
    { id: "addressComments", uses: "builtins.address-comments", displayName: "Address comments" }
  ] };
}

// Convert only an editor draft; the original persisted version is never rewritten.
export function toNodeDefinition(document: WorkflowDefinitionDocument): WorkflowDefinitionDocument {
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
  return { schema: workflowSchema, startStepId: entry(document.startStepId)!, steps };
}

export function definitionToGraph(original: WorkflowDefinitionDocument): { nodes: WorkflowStepNode[]; edges: Edge[] } {
  const document = toNodeDefinition(original);
  const nodes: WorkflowStepNode[] = document.steps.map((step, index) => ({
    id: step.id, type: "workflowStep", position: document.editor?.positions[step.id] ?? { x: (index % 3) * 280, y: Math.floor(index / 3) * 200 + 80 },
    data: { stepId: step.id, displayName: step.displayName || step.id, uses: step.uses,
      aiSettingsId: step.aiSettingsId, model: step.model, trigger: step.trigger, loop: step.loop }
  }));
  const edges: Edge[] = [];
  for (const step of document.steps) {
    if (step.nextStepId) edges.push({ id: `${step.id}:next`, source: step.id, target: step.nextStepId,
      markerEnd: { type: MarkerType.ArrowClosed }, style: step.nextStepPort === "return" ? { strokeDasharray: "6 4", stroke: "#986c26" } : undefined,
      sourceHandle: step.uses === loopUses ? "exit" : "next", targetHandle: step.nextStepPort || "input",
      label: step.nextStepPort === "return" ? "Return" : step.uses === loopUses ? "Exit" : undefined });
    if (step.loop?.bodyStepId) edges.push({ id: `${step.id}:body`, source: step.id, sourceHandle: "body",
      markerEnd: { type: MarkerType.ArrowClosed }, target: step.loop.bodyStepId, targetHandle: "input", label: "Body" });
  }
  return { nodes, edges };
}

export function graphToDefinition(nodes: WorkflowStepNode[], edges: Edge[], _schema: string, startStepId: string): WorkflowDefinitionDocument {
  return { schema: workflowSchema, startStepId, editor: { positions: Object.fromEntries(nodes.map(node => [node.id, node.position])) }, steps: nodes.map(node => {
    const next = edges.find(edge => edge.source === node.id && edge.sourceHandle !== "body");
    const body = edges.find(edge => edge.source === node.id && edge.sourceHandle === "body");
    return { id: node.data.stepId || node.id, uses: node.data.uses, displayName: node.data.displayName,
      nextStepId: next?.target ?? null, nextStepPort: next?.targetHandle === "return" ? "return" : null,
      aiSettingsId: node.data.aiSettingsId || undefined, model: node.data.model || undefined,
      trigger: node.data.uses === triggerUses ? node.data.trigger : undefined,
      loop: node.data.uses === loopUses && node.data.loop ? { ...node.data.loop, bodyStepId: body?.target ?? "" } : undefined };
  }) };
}

export function getEnabledDefinitionVersions(definitions: WorkflowDefinitionResponse[]) {
  const versions: Array<{ definition: WorkflowDefinitionResponse; version: WorkflowDefinitionVersionResponse }> = [];
  for (const definition of definitions) {
    for (const version of definition.versions) {
      if (version.isEnabled) {
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
