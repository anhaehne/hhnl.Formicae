const paths = {
  undo: "M9 5 4 10l5 5M4 10h10a6 6 0 0 1 0 12",
  redo: "m15 5 5 5-5 5m5-5H10a6 6 0 0 0 0 12",
  delete: "M4 7h16M9 7V4h6v3M6 7l1 14h10l1-14M10 11v6m4-6v6",
  duplicate: "M9 9h12v12H9zM5 15H3V3h12v2",
  arrange: "M3 3h6v6H3zM15 3h6v6h-6zM15 15h6v6h-6zM9 6h3v12h3",
  fit: "M9 3H3v6m12-6h6v6M3 15v6h6m12-6v6h-6",
  selection: "M3 8h5V3m8 0v5h5M3 16h5v5m8 0v-5h5M10 10h4v4h-4z",
  map: "m3 5 6-2 6 2 6-2v16l-6 2-6-2-6 2Zm6-2v16m6-14v16",
  search: "M21 21l-5-5M18 10a8 8 0 1 1-16 0 8 8 0 0 1 16 0",
  problems: "M12 3 2 21h20ZM12 9v5m0 3v1",
};

export function ToolbarIcon({ name }: { name: keyof typeof paths }) {
  return <svg className="editor-toolbar-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={paths[name]} /></svg>;
}
