import type { Edge, Node } from "@xyflow/react";
import type { WorkflowDefinitionDocument, WorkflowDefinitionResponse, WorkflowDefinitionTrigger, WorkflowDefinitionVersionResponse } from "./api";

export type WorkflowStepNodeData = {
  stepId: string;
  displayName: string;
  uses: string;
  [key: string]: unknown;
};

export type WorkflowStepNode = Node<WorkflowStepNodeData, "workflowStep">;

export const workflowSchema = "formicae.workflow/v1alpha1";

export const supportedUses = [
  "builtins.plan",
  "builtins.implement",
  "builtins.create-pull-request",
  "builtins.address-comments"
] as const;

export function createDefaultDefinitionDocument(): WorkflowDefinitionDocument {
  return {
    schema: workflowSchema,
    startStepId: "plan",
    steps: [
      { id: "plan", uses: "builtins.plan", nextStepId: "implement", displayName: "Plan" },
      { id: "implement", uses: "builtins.implement", nextStepId: "createPullRequest", displayName: "Implement" },
      { id: "createPullRequest", uses: "builtins.create-pull-request", nextStepId: "addressComments", displayName: "Create pull request" },
      { id: "addressComments", uses: "builtins.address-comments", nextStepId: null, displayName: "Address comments" }
    ]
  };
}

export function definitionToGraph(document: WorkflowDefinitionDocument): { nodes: WorkflowStepNode[]; edges: Edge[] } {
  const nodes: WorkflowStepNode[] = document.steps.map((step, index) => ({
    id: step.id,
    type: "workflowStep",
    position: { x: index * 240, y: 80 },
    data: {
      stepId: step.id,
      displayName: step.displayName || step.id,
      uses: step.uses
    }
  }));

  const knownIds = new Set(document.steps.map(step => step.id));
  const edges = document.steps
    .filter(step => step.nextStepId && knownIds.has(step.nextStepId))
    .map(step => ({
      id: `${step.id}->${step.nextStepId}`,
      source: step.id,
      target: step.nextStepId!
    }));

  return { nodes, edges };
}

export function graphToDefinition(
  nodes: WorkflowStepNode[],
  edges: Edge[],
  schema: string,
  startStepId: string,
  triggers?: WorkflowDefinitionTrigger[]
): WorkflowDefinitionDocument {
  const outgoingBySource = new Map<string, Edge>();
  for (const edge of edges) {
    if (!outgoingBySource.has(edge.source)) {
      outgoingBySource.set(edge.source, edge);
    }
  }

  return {
    schema,
    startStepId,
    triggers: triggers && triggers.length > 0 ? triggers : undefined,
    steps: nodes.map(node => ({
      id: node.data.stepId || node.id,
      uses: node.data.uses || "builtins.plan",
      nextStepId: outgoingBySource.get(node.id)?.target ?? null,
      displayName: node.data.displayName || node.data.stepId || node.id
    }))
  };
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
