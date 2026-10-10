import { inputsFor, outputsFor } from "../workflowData";
import type { Edge } from "@xyflow/react";
import { decisionPorts, loopUses, isStartUses, parallelUses, decisionUses, type WorkflowStepNode } from "../workflowGraph";
export async function arrange(nodes: WorkflowStepNode[], edges: Edge[]): Promise<WorkflowStepNode[]> {
  const { default: ELK } = await import("elkjs/lib/elk.bundled.js");
  const graph = await new ELK().layout({
    id: "workflow", layoutOptions: { "elk.algorithm": "layered", "elk.direction": "RIGHT", "elk.spacing.nodeNode": "70", "elk.layered.spacing.nodeNodeBetweenLayers": "110" },
    children: nodes.map(node => {
      const rows = Math.max(inputsFor(node).length, outputsFor(node).length);
      const controls = node.data.uses === parallelUses ? (node.data.parallel?.branchStepIds.length ?? 2) + 1 : node.data.decision ? decisionPorts(node.data.decision).length : node.data.uses === loopUses ? 2 : 1;
      return { id: node.id, width: node.data.variable ? 180 : 240, height: node.data.variable ? node.measured?.height ?? 100 : node.measured?.height ?? 155 + (rows + controls) * 28 + (node.data.executionStatus ? 46 : 0),
      layoutOptions: { "elk.portConstraints": "FIXED_ORDER" },
      ports: node.data.variable ? [] : [ ...(!isStartUses(node.data.uses) ? [{ id: `${node.id}:input`, properties: { "port.side": "WEST" } }] : []),
        ...(node.data.uses === decisionUses ? decisionPorts(node.data.decision!).map(item => ({ id: `${node.id}:${item.port}`, properties: { "port.side": "EAST" } })) : node.data.uses === parallelUses ? [{ id: `${node.id}:join`, properties: { "port.side": "NORTH" } }, ...(node.data.parallel?.branchStepIds ?? ["", ""]).map((_, index) => ({ id: `${node.id}:branch:${index}`, properties: { "port.side": "EAST" } })), { id: `${node.id}:next`, properties: { "port.side": "EAST" } }] : node.data.uses === loopUses ? [ { id: `${node.id}:return`, properties: { "port.side": "NORTH" } }, { id: `${node.id}:body`, properties: { "port.side": "EAST" } }, { id: `${node.id}:exit`, properties: { "port.side": "EAST" } } ] : [{ id: `${node.id}:next`, properties: { "port.side": "EAST" } }]) ] }; }),
    // Join edges are feedback to the barrier, not forward layout dependencies.
    edges: edges.filter(edge => !edge.sourceHandle?.startsWith("output:") && edge.targetHandle !== "join" && nodes.some(n => n.id === edge.source) && nodes.some(n => n.id === edge.target)).map(edge => ({ id: edge.id, sources: [`${edge.source}:${edge.sourceHandle || "next"}`], targets: [`${edge.target}:${edge.targetHandle || "input"}`] }))
  });
  return nodes.map(node => { const placed = graph.children?.find(child => child.id === node.id); return placed ? { ...node, position: { x: placed.x ?? 0, y: placed.y ?? 0 } } : node; });
}
