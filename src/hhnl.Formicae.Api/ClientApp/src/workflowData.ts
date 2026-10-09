import type { CustomTaskDefinition, CustomTaskInputDefinition, CustomTaskOutputDefinition } from "./api";
import type { WorkflowStepNode } from "./workflowGraph";
import { eventDefinition } from "./workflowEvents";
export const issueCommentUses = "github.add-issue-comment";
export const issueCommentInputs: CustomTaskInputDefinition[] = [{ name: "issueId", valueType: "number", required: true }, { name: "text", valueType: "string", required: true }];
export function inputsFor(node: WorkflowStepNode, tasks: CustomTaskDefinition[] = []): CustomTaskInputDefinition[] {
  return node.data.uses === issueCommentUses ? issueCommentInputs : (node.data.customTask?.definition ?? tasks.find(task => task.id === node.data.customTask?.taskId) ?? node.data.customTask?.snapshot)?.inputs ?? [];
}
export function outputsFor(node: WorkflowStepNode, tasks: CustomTaskDefinition[] = []): CustomTaskOutputDefinition[] {
  return eventDefinition(node.data.uses)?.outputs ?? (node.data.uses === "builtins.script" ? [{ name: "output", valueType: "string", required: true }] : (node.data.customTask?.definition ?? tasks.find(task => task.id === node.data.customTask?.taskId) ?? node.data.customTask?.snapshot)?.outputs ?? []);
}
