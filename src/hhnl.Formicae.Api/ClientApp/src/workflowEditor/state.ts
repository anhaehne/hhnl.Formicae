import { useRef, useState } from "react";
import type { Edge } from "@xyflow/react";
import type { WorkflowStepNode } from "../workflowGraph";

export type EditorDraft = { name: string; version: string; enabled: boolean; isDefault: boolean; start: string; nodes: WorkflowStepNode[]; edges: Edge[] };
const equal = (a: EditorDraft, b: EditorDraft) => JSON.stringify(a) === JSON.stringify(b);
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
    draft, dirty: !equal(draft, baseline.current), canUndo: past.current.length > 0, canRedo: future.current.length > 0,
    begin: () => { if (!transaction.current) transaction.current = value.current; }, commit,
    update: (edit: (draft: EditorDraft) => EditorDraft) => { const next = edit(value.current); if (equal(next, value.current)) return; if (!transaction.current) remember(value.current); publish(next); },
    reset: (next: EditorDraft) => { past.current = []; future.current = []; transaction.current = undefined; baseline.current = next; publish(next); },
    saved: () => { commit(); baseline.current = value.current; refresh(n => n + 1); },
    undo: () => { commit(); const previous = past.current.pop(); if (previous) { future.current.push(value.current); publish(previous); } },
    redo: () => { commit(); const next = future.current.pop(); if (next) { past.current.push(value.current); publish(next); } }
  };
}
