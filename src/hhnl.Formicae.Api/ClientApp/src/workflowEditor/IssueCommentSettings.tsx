import type { ComponentProps } from "react";
import type { WorkflowIssueCommentSettings } from "../api";
import { issueCommentInputs } from "../workflowData";
import { CustomTaskSettings } from "./CustomTaskSettings";
type Props = Omit<ComponentProps<typeof CustomTaskSettings>, "value" | "onChange" | "savedSnapshot" | "inline" | "action"> & { value?: WorkflowIssueCommentSettings | null; onChange: (value: WorkflowIssueCommentSettings) => void };
export function IssueCommentSettings({ value, onChange, ...props }: Props) {
  const snapshot = { id: "issue-comment", revision: 1, name: "Add issue comment", description: "Issue id is the issue number in this workflow’s connected GitHub repository.", promptTemplate: "", inputs: issueCommentInputs, outputs: [], runner: { kind: "agent" as const, timeoutSeconds: 1 } };
  return <CustomTaskSettings {...props} inline action value={{ taskId: snapshot.id, snapshot, inputs: value?.inputs ?? {}, bindings: value?.bindings }} onChange={next => onChange({ inputs: next.inputs ?? {}, bindings: next.bindings ?? {} })} />;
}
