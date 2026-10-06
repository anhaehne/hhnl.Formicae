import { useEffect, useMemo, useRef, useState } from "react";
import { getWorkflowLogPage, workflowLogUrl, type LogFilters, type WorkflowLog } from "../api";
import { describeWorkerLog } from "./evidence";
const MAX_LOGS = 2000;
export default function LogViewer({ workflowId, taskRunId, executionAttemptId, legacy = false }: { workflowId: string; taskRunId?: string; executionAttemptId?: string; legacy?: boolean }) {
  const [level, setLevel] = useState(""), [source, setSource] = useState(""), [search, setSearch] = useState("");
  const [logs, setLogs] = useState<WorkflowLog[]>([]), [hasEarlier, setHasEarlier] = useState(false), [loading, setLoading] = useState(false), [ready, setReady] = useState(false);
  const [follow, setFollow] = useState(true), [connection, setConnection] = useState("Connecting"), [error, setError] = useState<string>(), [bounded, setBounded] = useState(false), [reload, setReload] = useState(0);
  const lastScope = useRef(""), cursor = useRef(0), earliest = useRef(0), viewport = useRef<HTMLDivElement>(null), generation = useRef(0), pending = useRef<AbortController | undefined>(undefined);
  const filters = useMemo<LogFilters>(() => ({ taskRunId, executionAttemptId, level, source, search }), [taskRunId, executionAttemptId, level, source, search]);
  useEffect(() => {
    const version = ++generation.current, controller = new AbortController(); pending.current?.abort(); pending.current = controller;
    setReady(false); setLoading(true); setError(undefined);
    const scopeKey = `${workflowId}:${JSON.stringify(filters)}`;
    if (lastScope.current !== scopeKey) { setLogs([]); setBounded(false); }
    lastScope.current = scopeKey; cursor.current = 0; earliest.current = 0;
    getWorkflowLogPage(workflowId, filters, {}, controller.signal).then(page => {
      if (version !== generation.current) return;
      setLogs(page.items); cursor.current = page.nextCursor; earliest.current = page.previousCursor; setHasEarlier(page.hasEarlier); setReady(true);
    }).catch(reason => { if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : "Could not load logs."); }).finally(() => { if (version === generation.current) setLoading(false); });
    return () => { controller.abort(); pending.current?.abort(); };
  }, [workflowId, filters, reload]);
  useEffect(() => {
    if (!ready || !follow) { setConnection(follow ? "Loading history" : "Follow paused"); return; }
    let events: EventSource | undefined, reconnectTimer: number | undefined, stopped = false, retryDelay = 1000;
    const append = (event: MessageEvent) => {
      try {
        const entry = JSON.parse(event.data) as WorkflowLog;
        if (!Number.isSafeInteger(entry.sequence) || entry.sequence <= 0 || typeof entry.message !== "string") return;
        cursor.current = Math.max(cursor.current, entry.sequence);
        setLogs(current => {
          if (current.some(log => log.sequence === entry.sequence)) return current;
          const all = [...current, entry].sort((a, b) => a.sequence - b.sequence);
          if (all.length > MAX_LOGS) { setBounded(true); setHasEarlier(true); }
          const next = all.slice(-MAX_LOGS); earliest.current = next[0]?.sequence ?? 0; return next;
        });
      } catch { setError("Received an invalid log event. Reconnect to recover persisted logs."); }
    };
    const heartbeat = (event: MessageEvent) => { try { const value = JSON.parse(event.data) as { cursor?: number }; if (Number.isSafeInteger(value.cursor)) cursor.current = Math.max(cursor.current, value.cursor!); } catch { /* heartbeat carries no evidence */ } };
    function connect() {
      if (stopped) return;
      events = new EventSource(workflowLogUrl(workflowId, "stream", filters, cursor.current));
      setConnection("Connecting");
      events.onopen = () => { retryDelay = 1000; setConnection("Live"); setError(undefined); };
      events.addEventListener("log", append);
      events.addEventListener("heartbeat", heartbeat);
      events.onerror = () => {
        events?.close(); setConnection("Reconnecting");
        reconnectTimer = window.setTimeout(connect, retryDelay);
        retryDelay = Math.min(retryDelay * 2, 10000);
      };
    }
    connect();
    return () => { stopped = true; events?.close(); if (reconnectTimer !== undefined) window.clearTimeout(reconnectTimer); };
  }, [workflowId, filters, follow, ready]);
  useEffect(() => { if (follow && viewport.current) viewport.current.scrollTop = viewport.current.scrollHeight; }, [logs, follow]);
  async function older() {
    const version = generation.current, controller = new AbortController(); pending.current?.abort(); pending.current = controller;
    const height = viewport.current?.scrollHeight ?? 0; setFollow(false); setLoading(true); setError(undefined);
    try {
      const page = await getWorkflowLogPage(workflowId, filters, { before: earliest.current }, controller.signal);
      if (version !== generation.current) return;
      setLogs(current => { const all = [...page.items, ...current].filter((log, index, items) => items.findIndex(item => item.sequence === log.sequence) === index).sort((a, b) => a.sequence - b.sequence); if (all.length > MAX_LOGS) setBounded(true); return all.slice(0, MAX_LOGS); });
      earliest.current = page.previousCursor; setHasEarlier(page.hasEarlier);
      requestAnimationFrame(() => { if (viewport.current) viewport.current.scrollTop = viewport.current.scrollHeight - height; });
    } catch (reason) { if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : "Could not load older logs."); }
    finally { if (version === generation.current) setLoading(false); }
  }
  return <section className="execution-logs" aria-label="Worker logs">
    <div className="execution-toolbar"><h3>Worker logs</h3><span role="status">{connection}</span><button type="button" onClick={() => { if (!follow) setReload(value => value + 1); setFollow(value => !value); }}>{follow ? "Pause follow" : "Resume follow"}</button><a href={workflowLogUrl(workflowId, "download", filters)} download>Download filtered logs</a></div>
    <div className="execution-filters"><label>Severity<select aria-label="Severity" value={level} onChange={event => setLevel(event.target.value)}><option value="">All severities</option>{["Debug", "Information", "Warning", "Error", "Critical"].map(value => <option key={value}>{value}</option>)}</select></label><label>Stream<select aria-label="Stream" value={source} onChange={event => setSource(event.target.value)}><option value="">All streams</option>{["stdout", "stderr", "worker-error", "system", "runtime", "worker", "worker-checkpoint"].map(value => <option key={value}>{value}</option>)}</select></label><label>Search logs<input value={search} onChange={event => setSearch(event.target.value)} placeholder="Filter persisted log text" /></label></div>
    {legacy && <p className="muted">Legacy worker logs have no attempt identity; this view includes every attempt of this task.</p>}
    {error && <div><p role="alert" className="error-text">{error}</p><button type="button" onClick={() => setReload(value => value + 1)}>Retry log history</button></div>}
    {bounded && <p className="muted">Showing up to {MAX_LOGS} entries in memory. Load earlier pages or download to investigate more.</p>}
    <button type="button" disabled={loading || !hasEarlier} onClick={() => void older()}>{loading ? "Loading logs…" : "Load older logs"}</button>
    <div className="execution-log-viewport" ref={viewport} tabIndex={0} aria-label="Log entries">
      {logs.map(log => { const readable = describeWorkerLog(log.message); return <article className={`execution-log-entry level-${log.level.toLowerCase()}`} key={log.sequence || log.id}><div className="execution-log-meta"><time dateTime={log.createdAt}>{new Date(log.createdAt).toLocaleString()}</time><span>{log.level}</span><span>{log.source ?? "system"}</span><span>#{log.sequence}</span>{log.externalId && <span title={log.externalId}>{log.externalId}</span>}</div>{readable.title && <strong>{readable.title}</strong>}<pre>{readable.text}</pre>{readable.structured && <details><summary>Raw worker payload</summary><pre>{log.message}</pre></details>}</article>; })}
      {!loading && logs.length === 0 && <p className="muted">No logs match this scope and filters.</p>}
    </div>
  </section>;
}
