import type { Node, NodeChange, NodeProps } from "@xyflow/react";
import type { WorkflowEditorGroup } from "../api";
import type { WorkflowStepNode } from "../workflowGraph";

export const groupColors: Record<string, string> = {
  gray: "#e2e8f0", blue: "#dbeafe", green: "#dcfce7", yellow: "#fef9c3",
  orange: "#ffedd5", purple: "#f3e8ff", pink: "#fce7f3"
};
type GroupNode = Node<{ name: string; color: string }, "workflowGroup">;
export const groupNodeId = (id: string) => `group:${id}`;
export function WorkflowGroupNode({ data }: NodeProps<GroupNode>) {
  return <div className="editor-group-container" style={{ backgroundColor: groupColors[data.color] ?? groupColors.gray }}>
    <div className="editor-group-title" title={data.name}>{data.name}</div>
  </div>;
}

// Keep absolute task positions in the saved graph; translate only at the React Flow boundary.
export function groupBounds(group: WorkflowEditorGroup, nodes: WorkflowStepNode[], measurements: Record<string, { width: number; height: number }>) {
  const members = nodes.filter(node => group.nodeIds.includes(node.id));
  const x = Math.min(...members.map(node => node.position.x)) - 24;
  const y = Math.min(...members.map(node => node.position.y)) - 56;
  const right = Math.max(...members.map(node => node.position.x + (measurements[node.id]?.width ?? 300))) + 24;
  const bottom = Math.max(...members.map(node => node.position.y + (measurements[node.id]?.height ?? 150))) + 24;
  return { x, y, width: Math.max(200, right - x), height: Math.max(100, bottom - y) };
}
export function cleanGroups(groups: WorkflowEditorGroup[], nodes: WorkflowStepNode[]) {
  const assigned = new Set<string>(), ids = new Set<string>();
  return groups.flatMap(group => {
    if (!group.id || ids.has(group.id)) return [];
    ids.add(group.id);
    const nodeIds = group.nodeIds.filter(id => {
      if (assigned.has(id) || !nodes.some(node => node.id === id)) return false;
      assigned.add(id); return true;
    });
    return nodeIds.length ? [{ ...group, name: group.name.trim() ? group.name : "Group", color: groupColors[group.color] ? group.color : "gray", nodeIds }] : [];
  });
}
export function groupedNodes(nodes: WorkflowStepNode[], groups: WorkflowEditorGroup[], measurements: Record<string, { width: number; height: number }>, selected: Set<string>): (WorkflowStepNode | GroupNode)[] {
  const containers: GroupNode[] = groups.map(group => {
    const bounds = groupBounds(group, nodes, measurements);
    return { id: groupNodeId(group.id), type: "workflowGroup", position: { x: bounds.x, y: bounds.y },
      style: { width: bounds.width, height: bounds.height }, data: { name: group.name, color: group.color },
      selected: selected.has(groupNodeId(group.id)), connectable: false, dragHandle: ".editor-group-title", zIndex: -1 };
  });
  return [...containers, ...nodes.map(node => {
    const group = groups.find(group => group.nodeIds.includes(node.id));
    const parent = group && containers.find(parent => parent.id === groupNodeId(group.id));
    return { ...node, measured: measurements[node.id], selected: selected.has(node.id), zIndex: 1,
      parentId: parent?.id, position: parent ? { x: node.position.x - parent.position.x, y: node.position.y - parent.position.y } : node.position };
  })];
}
export function moveGroupedNodes(nodes: WorkflowStepNode[], groups: WorkflowEditorGroup[], measurements: Record<string, { width: number; height: number }>, changes: NodeChange[]) {
  const bounds = new Map(groups.map(group => [group.id, groupBounds(group, nodes, measurements)]));
  return nodes.map(node => {
    const group = groups.find(group => group.nodeIds.includes(node.id));
    const parent = group && bounds.get(group.id);
    const parentChange = group && changes.find(change => change.type === "position" && change.id === groupNodeId(group.id));
    const change = changes.find(change => change.type === "position" && change.id === node.id);
    if (parent && parentChange?.type === "position" && parentChange.position) {
      return { ...node, position: { x: node.position.x + parentChange.position.x - parent.x, y: node.position.y + parentChange.position.y - parent.y } };
    }
    if (change?.type === "position" && change.position) return { ...node, position: { x: change.position.x + (parent?.x ?? 0), y: change.position.y + (parent?.y ?? 0) } };
    return node;
  });
}
