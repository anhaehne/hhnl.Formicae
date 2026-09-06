import { triggerUses, parallelUses, decisionUses } from "../workflowGraph";
import { catalog } from "./catalog";

export function StepIcon({ uses }: { uses: string }) {
  if (uses === decisionUses) return <svg className="editor-step-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="m12 2 10 10-10 10L2 12Z" /><path d="M8 12h8m-4-4 4 4-4 4" /></svg>;
  if (uses === parallelUses) return <svg className="editor-step-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M3 12h4m0 0V5h7m-7 7v7h7m0-14 3 3-3 3m0 2 3 3-3 3M17 8h4v8h-4" /></svg>;
  if (uses === triggerUses) return <svg className="editor-step-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="m13 2-9 12h7l-1 8 10-13h-7z" /></svg>;
  return <span aria-hidden="true">{catalog.find(item => item.uses === uses)?.icon}</span>;
}
