import { useRef, useState } from "react";
import type { EnvironmentSnapshot } from "../api";
import type { Edge } from "@xyflow/react";
import type { WorkflowStepNode } from "../workflowGraph";

export type EditorDraft = { defaultEnvironmentId?: string | null; defaultEnvironmentSnapshot?: EnvironmentSnapshot | null; name: string; version: string; defaultPersonaId?: string | null; enabled: boolean; isDefault: boolean; start: string; nodes: WorkflowStepNode[]; edges: Edge[] };
const comparable = (draft: EditorDraft) => ({ ...draft, defaultEnvironmentSnapshot: undefined, nodes: draft.nodes.map(node => ({ ...node, data: { ...node.data, personaSnapshot: undefined, environmentSnapshot: undefined, customTask: node.data.customTask ? { ...node.data.customTask, snapshot: undefined } : node.data.customTask } })) });
const equal = (a: EditorDraft, b: EditorDraft) => JSON.stringify(comparable(a)) === JSON.stringify(comparable(b));
export function useEditorState(initial: EditorDraft) {
  const [draft, render] = useState(initial);
  const value = useRef(initial), baseline = useRef(initial);
  const past = useRef<EditorDraft[]>([]), future = useRef<EditorDraft[]>([]);
  const transaction = useRef<EditorDraft | undefined>(undefined);
  const [, refresh] = useState(0);
  const publish = (next: EditorDraft) => { value.current = next; render(next); refresh(n => n + 1); };
  const remember = (before: EditorDraft) => { past.current = [...past.current.slice(-99), before]; future.current = []; };
  const commit = () => { if (transaction.current && !equal(transaction.current, value.current)) remember(transaction.current); transaction.current = undefined; refresh(n => n + 1); };
  return {
    draft, savedDraft: baseline.current, dirty: !equal(draft, baseline.current), canUndo: past.current.length > 0, canRedo: future.current.length > 0,
    begin: () => { if (!transaction.current) transaction.current = value.current; }, commit,
    update: (edit: (draft: EditorDraft) => EditorDraft) => { const next = edit(value.current); if (equal(next, value.current)) return; if (!transaction.current) remember(value.current); publish(next); },
    reset: (next: EditorDraft) => { past.current = []; future.current = []; transaction.current = undefined; baseline.current = next; publish(next); },
    saved: (submitted: EditorDraft, authoritative: EditorDraft) => {
      commit(); baseline.current = authoritative;
      // Only the submitted draft was saved. Later edits keep their own dirty state.
      if (equal(value.current, submitted)) publish(authoritative);
      else publish({ ...value.current, defaultEnvironmentSnapshot: value.current.defaultEnvironmentId === submitted.defaultEnvironmentId ? authoritative.defaultEnvironmentSnapshot : value.current.defaultEnvironmentSnapshot, nodes: value.current.nodes.map(node => {
        const before = submitted.nodes.find(item => item.id === node.id), saved = authoritative.nodes.find(item => item.id === node.id);
        if (!before || !saved || node.data.uses !== before.data.uses) return node;
        const personaUnchanged = node.data.personaId === before.data.personaId && value.current.defaultPersonaId === submitted.defaultPersonaId;
        const environmentUnchanged = node.data.environmentId === before.data.environmentId && (node.data.environmentId != null || value.current.defaultEnvironmentId === submitted.defaultEnvironmentId);
        const taskUnchanged = node.data.customTask?.taskId === before.data.customTask?.taskId;
        return { ...node, data: { ...node.data,
          environmentSnapshot: environmentUnchanged ? saved.data.environmentSnapshot : node.data.environmentSnapshot,
          personaSnapshot: personaUnchanged ? saved.data.personaSnapshot : node.data.personaSnapshot,
          customTask: node.data.customTask && taskUnchanged ? { ...node.data.customTask, snapshot: saved.data.customTask?.snapshot } : node.data.customTask
        } };
      }) });
    },
    undo: () => { commit(); const previous = past.current.pop(); if (previous) { future.current.push(value.current); publish(previous); } },
    redo: () => { commit(); const next = future.current.pop(); if (next) { past.current.push(value.current); publish(next); } }
  };
}
