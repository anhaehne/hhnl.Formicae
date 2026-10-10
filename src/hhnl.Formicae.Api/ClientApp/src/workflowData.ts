import type { CustomTaskDefinition, CustomTaskInputDefinition, CustomTaskOutputDefinition } from "./api";
import type { WorkflowStepNode } from "./workflowGraph";
import { eventDefinition } from "./workflowEvents";
export const variableUses = "builtins.variable";
export const waitUses = "github.issue-commented";
export const waitOutputs = ["commentId", "body", "author", "url", "createdAt"].map(name => ({ name, valueType: "string" as const, required: true }));
export const waitSchema = { inputs: [{ name: "issueNumber", valueType: "number" as const, required: true }], outputs: waitOutputs };
export const issueCommentUses = "github.add-issue-comment";
export const issueCommentInputs: CustomTaskInputDefinition[] = [{ name: "issueId", valueType: "number", required: true }, { name: "text", valueType: "string", required: true }];
export function inputsFor(node: WorkflowStepNode, tasks: CustomTaskDefinition[] = []): CustomTaskInputDefinition[] {
  return node.data.decision?.inputType ? [{ name: "value", valueType: node.data.decision.inputType === "any" ? "boolean" : node.data.decision.inputType, required: true }] : node.data.variable ? [{ name: "value", valueType: node.data.variable.valueType, required: false }] : node.data.uses === waitUses ? waitSchema.inputs : node.data.uses === issueCommentUses ? issueCommentInputs : (node.data.customTask?.definition ?? tasks.find(task => task.id === node.data.customTask?.taskId) ?? node.data.customTask?.snapshot)?.inputs ?? [];
}
export function outputsFor(node: WorkflowStepNode, tasks: CustomTaskDefinition[] = []): CustomTaskOutputDefinition[] {
  return node.data.variable ? [{ name: "value", valueType: node.data.variable.valueType, required: false }] : node.data.uses === waitUses ? waitSchema.outputs : eventDefinition(node.data.uses)?.outputs ?? (node.data.uses === "builtins.script" ? [{ name: "output", valueType: "string", required: true }] : (node.data.customTask?.definition ?? tasks.find(task => task.id === node.data.customTask?.taskId) ?? node.data.customTask?.snapshot)?.outputs ?? []);
}
