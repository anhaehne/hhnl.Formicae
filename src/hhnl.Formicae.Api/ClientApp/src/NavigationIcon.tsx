const paths = {
  workflows: "M9 5 19 12 9 19ZM3 5v14",
  "workflow-definitions": "M3 3h6v6H3zM15 15h6v6h-6zM9 6h6a3 3 0 0 1 3 3v6M6 9v9h9",
  integrations: "m8 3 4 4-5 5-4-4Zm4 14 5-5 4 4-5 5ZM10 10l4 4M2 2l3 3m14 14 3 3",
  repositories: "M4 4h6l2 3h8v14H4ZM4 11h16",
  users: "M16 21v-3a4 4 0 0 0-4-4H7a4 4 0 0 0-4 4v3M13 6a4 4 0 1 1-8 0 4 4 0 0 1 8 0M17 3a4 4 0 0 1 0 8m2 3a4 4 0 0 1 3 4v3",
  "custom-tasks": "M4 3h12l4 4v14H4ZM8 10l-2 3 2 3m8-6 2 3-2 3m-3-6-2 6",
  environments: "M3 5h18v14H3ZM3 10h18M7 7.5h.1m4 0h.1M7 14h10",
  personas: "M20 21v-2a7 7 0 0 0-14 0v2M17 7a5 5 0 1 1-10 0 5 5 0 0 1 10 0M2 3h3M2 7h2",
  settings: "M4 3v18M12 3v18M20 3v18M1 8h6m2 8h6m2-9h6",
};

export function NavigationIcon({ name }: { name: keyof typeof paths }) {
  return <svg className="navigation-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={paths[name]} /></svg>;
}
