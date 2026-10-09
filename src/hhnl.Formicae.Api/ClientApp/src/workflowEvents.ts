import type { WorkflowEventDescriptor, WorkflowEventSettings, WorkflowDefinitionStep } from "./api";

// Metadata is supplied by registered application/integration event definitions.
export const eventDefinitions: WorkflowEventDescriptor[] = [];
export function registerEventDefinitions(definitions: WorkflowEventDescriptor[]) {
  eventDefinitions.splice(0, eventDefinitions.length, ...definitions);
}
export const eventDefinition = (uses: string) => eventDefinitions.find(item => item.uses === uses);
export const isEventUses = (uses: string) => uses === "builtins.start" || uses === "builtins.trigger" || (!!eventDefinition(uses) && !eventDefinition(uses)?.callable);
export const defaultEventSettings = (uses: string): WorkflowEventSettings => ({ enabled: true,
  ...Object.fromEntries((eventDefinition(uses)?.fields ?? []).map(field => [field.name, field.kind === "repositories" ? [] : ""])) });
export function adaptEventStep(step: WorkflowDefinitionStep): WorkflowDefinitionStep {
  if (step.event || !step.trigger) return step;
  const { type, ...configuration } = step.trigger;
  const uses = type === "Manual" ? "builtins.start" : type === "Webhook" ? "builtins.webhook" : "compat.issue-label";
  return { ...step, uses, trigger: undefined, event: configuration };
}
