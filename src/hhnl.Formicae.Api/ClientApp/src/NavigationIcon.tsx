const paths = {
  workflows: "M9 5 19 12 9 19ZM3 5v14",
  "workflow-definitions": "M3 3h6v6H3zM15 15h6v6h-6zM9 6h6a3 3 0 0 1 3 3v6M6 9v9h9",
  integrations: "m8 3 4 4-5 5-4-4Zm4 14 5-5 4 4-5 5ZM10 10l4 4M2 2l3 3m14 14 3 3",
  repositories: "M4 4h6l2 3h8v14H4ZM4 11h16",
  users: "M16 21v-3a4 4 0 0 0-4-4H7a4 4 0 0 0-4 4v3M13 6a4 4 0 1 1-8 0 4 4 0 0 1 8 0M17 3a4 4 0 0 1 0 8m2 3a4 4 0 0 1 3 4v3",
  settings: "M4 3v18M12 3v18M20 3v18M1 8h6m2 8h6m2-9h6",
};

export function NavigationIcon({ name }: { name: keyof typeof paths }) {
  return <svg className="navigation-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={paths[name]} /></svg>;
}
