export function enumName(value: string | number | undefined, names: string[]) { return typeof value === "number" ? names[value] ?? String(value) : value ?? "Unknown"; }
export const workflowStates = ["Queued", "Planning", "Implementing", "CreatingPullRequest", "Reviewing", "Completed", "Failed", "Canceled", "Running"];
export const taskStates = ["Queued", "Running", "Succeeded", "Failed", "Canceled"];
export function duration(start?: string | null, end?: string | null) { if (!start) return "Not started"; const seconds = Math.max(0, Math.floor(((end ? Date.parse(end) : Date.now()) - Date.parse(start)) / 1000)); if (!Number.isFinite(seconds)) return "Duration unavailable"; return seconds < 60 ? `${seconds}s` : seconds >= 3600 ? `${Math.floor(seconds / 3600)}h ${Math.floor(seconds / 60) % 60}m` : `${Math.floor(seconds / 60)}m ${seconds % 60}s`; }
export function parseEvidence<T>(raw?: string | null): T | undefined { if (!raw) return undefined; try { return JSON.parse(raw) as T; } catch { return undefined; } }
export function Evidence({ title, value, raw = false }: { title: string; value: unknown; raw?: boolean }) { const content = raw ? String(value ?? "") : JSON.stringify(value ?? null, null, 2); return <details className="execution-evidence"><summary>{title}</summary><pre>{content}</pre></details>; }
export function StateBadge({ value }: { value: string }) { return <span className={`status-badge status-${value.toLowerCase()}`}>{value}</span>; }
// Worker events are telemetry. They never become task output.
export function describeWorkerLog(message: string): { title?: string; text: string; structured: boolean } {
  const payload = parseEvidence<Record<string, unknown>>(message);
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) return { text: message, structured: false };
  const object = (value: unknown) => value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : {};
  const item = object(payload.item), observation = object(payload.observation), args = object(payload.args), content = object(payload.content), messageObject = object(payload.message);
  const title = [payload.type ?? payload.event ?? payload.action, item.type].filter(value => typeof value === "string").join(" · ");
  const pieces = [item.command, item.text, item.aggregated_output, payload.text, typeof payload.message === "string" ? payload.message : messageObject.content, typeof payload.content === "string" ? payload.content : content.text, observation.content, args.command, args.content].filter(value => typeof value === "string" && value !== "") as string[];
  return { title: title || "Worker event", text: [...new Set(pieces)].join("\n") || message, structured: true };
}
