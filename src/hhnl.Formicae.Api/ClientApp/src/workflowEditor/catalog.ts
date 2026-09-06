import { loopUses, triggerUses, parallelUses, decisionUses } from "../workflowGraph";
export const catalog = [
  { uses: "builtins.plan", title: "Plan", icon: "◈", description: "Create a plan for the work item." },
  { uses: "builtins.implement", title: "Implement", icon: "⌘", description: "Implement the planned changes." },
  { uses: "builtins.create-pull-request", title: "Create pull request", icon: "↗", description: "Open a pull request for the changes." },
  { uses: "builtins.address-comments", title: "Address comments", icon: "☰", description: "Respond to pull request feedback." },
  { uses: triggerUses, title: "Trigger", icon: "ϟ", description: "Start this workflow when an issue label is added." },
  { uses: decisionUses, title: "Decision", icon: "◇", description: "Choose the True or False route using a typed condition." },
  { uses: parallelUses, title: "Parallel", icon: "⑂", description: "Run independent Plan branches together, then join." },
  { uses: loopUses, title: "Loop", icon: "↻", description: "Repeat a connected task sequence a fixed number of times." }
];
export const titleFor = (uses: string) => catalog.find(item => item.uses === uses)?.title ?? uses;
