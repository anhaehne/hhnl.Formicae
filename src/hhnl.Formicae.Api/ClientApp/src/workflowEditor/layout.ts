import type { Edge } from "@xyflow/react";
import { loopUses, triggerUses, type WorkflowStepNode } from "../workflowGraph";
export async function arrange(nodes: WorkflowStepNode[], edges: Edge[]): Promise<WorkflowStepNode[]> {
  const { default: ELK } = await import("elkjs/lib/elk.bundled.js");
  const graph = await new ELK().layout({
    id: "workflow", layoutOptions: { "elk.algorithm": "layered", "elk.direction": "RIGHT", "elk.spacing.nodeNode": "70", "elk.layered.spacing.nodeNodeBetweenLayers": "110" },
    children: nodes.map(node => ({ id: node.id, width: 240, height: node.data.uses === loopUses ? 160 : 120,
      layoutOptions: { "elk.portConstraints": "FIXED_ORDER" },
      ports: [ ...(node.data.uses !== triggerUses ? [{ id: `${node.id}:input`, properties: { "port.side": "WEST" } }] : []),
        ...(node.data.uses === loopUses ? [ { id: `${node.id}:return`, properties: { "port.side": "NORTH" } }, { id: `${node.id}:body`, properties: { "port.side": "EAST" } }, { id: `${node.id}:exit`, properties: { "port.side": "EAST" } } ] : [{ id: `${node.id}:next`, properties: { "port.side": "EAST" } }]) ] })),
    edges: edges.filter(edge => nodes.some(n => n.id === edge.source) && nodes.some(n => n.id === edge.target)).map(edge => ({ id: edge.id, sources: [`${edge.source}:${edge.sourceHandle || "next"}`], targets: [`${edge.target}:${edge.targetHandle || "input"}`] }))
  });
  return nodes.map(node => { const placed = graph.children?.find(child => child.id === node.id); return placed ? { ...node, position: { x: placed.x ?? 0, y: placed.y ?? 0 } } : node; });
}
