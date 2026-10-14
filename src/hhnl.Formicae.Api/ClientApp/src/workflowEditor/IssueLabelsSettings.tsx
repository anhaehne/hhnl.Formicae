import type { ComponentProps } from "react";
import type { WorkflowIssueLabelsSettings } from "../api";
import { issueLabelsInputs } from "../workflowData";
import { CustomTaskSettings } from "./CustomTaskSettings";
type Props = Omit<ComponentProps<typeof CustomTaskSettings>, "value" | "onChange" | "savedSnapshot" | "inline" | "action"> & { value?: WorkflowIssueLabelsSettings | null; onChange: (value: WorkflowIssueLabelsSettings) => void };
export function IssueLabelsSettings({ value, onChange, ...props }: Props) {
  const snapshot = { id: "issue-labels", revision: 1, name: "Add issue labels", description: "Issue id is the issue number in this workflow’s connected GitHub repository. Enter one label per line (up to 100 labels, 50 characters each). Existing labels are preserved.", promptTemplate: "", inputs: issueLabelsInputs, outputs: [], runner: { kind: "agent" as const, timeoutSeconds: 1 } };
  return <CustomTaskSettings {...props} inline action value={{ taskId: snapshot.id, snapshot, inputs: value?.inputs ?? {}, bindings: value?.bindings }} onChange={next => onChange({ inputs: next.inputs ?? {}, bindings: next.bindings ?? {} })} />;
}
