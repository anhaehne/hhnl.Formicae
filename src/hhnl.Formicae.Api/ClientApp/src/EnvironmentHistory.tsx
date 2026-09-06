export function EnvironmentHistory({ detailsJson }: { detailsJson?: string | null }) {
  let item: { id?: unknown; revision?: unknown; name?: unknown; timeoutLimitSeconds?: unknown } | undefined;
  try { const details = JSON.parse(detailsJson || "null"); if (details?.environment && typeof details.environment === "object") item = details.environment; } catch { return null; }
  if (!item || typeof item.id !== "string" || typeof item.name !== "string" || typeof item.revision !== "number") return null;
  return <section aria-label="Environment profile configuration"><h4>Environment profile configuration</h4><p>{item.name} · revision {item.revision}</p><p className="muted">Profile {item.id} · {typeof item.timeoutLimitSeconds === "number" ? `Saved timeout limit: ${item.timeoutLimitSeconds} seconds` : "No profile timeout limit"}. These are pinned profile constraints, not observed job settings.</p></section>;
}
