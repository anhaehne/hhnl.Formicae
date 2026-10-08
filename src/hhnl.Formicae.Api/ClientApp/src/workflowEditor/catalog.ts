import { eventDefinitions } from "../workflowEvents";
import { scriptUses, agentTaskUses, customTaskUses, loopUses, parallelUses, decisionUses } from "../workflowGraph";
export const taskCatalog = [
  { uses: scriptUses, title: "Script", icon: ">_", description: "Run a bounded shell script and retain output and exit code." },
  { uses: agentTaskUses, title: "Agent task", icon: "✧", description: "Run an agent prompt with a persona, typed inputs and outputs." },
  { uses: customTaskUses, title: "Custom task", icon: "✧", description: "Run a reusable agent prompt with typed inputs in a scratch workspace." },
  { uses: "builtins.plan", title: "Plan", icon: "◈", description: "Create a plan for the work item." },
  { uses: "builtins.implement", title: "Implement", icon: "⌘", description: "Implement the planned changes." },
  { uses: "builtins.create-pull-request", title: "Create pull request", icon: "↗", description: "Open a pull request for the changes." },
  { uses: "builtins.address-comments", title: "Address comments", icon: "☰", description: "Respond to pull request feedback." },
  { uses: decisionUses, title: "Decision", icon: "◇", description: "Choose the True or False route using a typed condition." },
  { uses: parallelUses, title: "Parallel", icon: "⑂", description: "Run independent Plan branches together, then join." },
  { uses: loopUses, title: "Loop", icon: "↻", description: "Repeat a connected task sequence a fixed number of times." }
];
export const getCatalog = () => [...taskCatalog, ...eventDefinitions.filter(item => !item.legacy).map(item => ({ ...item, icon: "ϟ" }))];
export const titleFor = (uses: string) => getCatalog().find(item => item.uses === uses)?.title ?? eventDefinitions.find(item => item.uses === uses)?.title ?? (uses === "builtins.start" ? "Start" : uses);
