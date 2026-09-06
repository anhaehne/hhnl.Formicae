import { triggerUses } from "../workflowGraph";
import { catalog } from "./catalog";

export function StepIcon({ uses }: { uses: string }) {
  if (uses === triggerUses) return <svg className="editor-step-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="m13 2-9 12h7l-1 8 10-13h-7z" /></svg>;
  return <span aria-hidden="true">{catalog.find(item => item.uses === uses)?.icon}</span>;
}
