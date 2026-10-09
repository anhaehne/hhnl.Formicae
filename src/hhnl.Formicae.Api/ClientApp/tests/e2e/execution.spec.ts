import { expect, test, type Page, type Route } from "@playwright/test";
let pageErrors: string[] = [];
test.beforeEach(async ({ page }) => { pageErrors = []; page.on("pageerror", error => pageErrors.push(error.message)); });
test.afterEach(async () => { expect(pageErrors).toEqual([]); });
const id = "11111111-1111-1111-1111-111111111111", runId = "22222222-2222-2222-2222-222222222222", currentAttempt = "33333333-3333-3333-3333-333333333333", priorAttempt = "44444444-4444-4444-4444-444444444444";
const time = "2026-10-06T10:00:00Z";
const workflow = { workflowId: id, issueUrl: "https://example.com/issues/1", repositoryUrl: "https://example.com/repo", status: "Running", currentStep: "Custom", currentDefinitionStepId: "summary", createdAt: time, updatedAt: time };
const definition = { schema: "formicae.workflow/v1alpha3", startStepId: "summary", editor: { positions: { summary: { x: 0, y: 0 }, route: { x: 360, y: 0 } } }, steps: [ { id: "summary", uses: "builtins.custom-task", displayName: "Summarize", nextStepId: "route", model: "saved-model", customTask: { taskId: "task", inputs: {}, snapshot: { id: "task", name: "Summary task", revision: 2, inputs: [], outputs: [] } } }, { id: "route", uses: "builtins.decision", displayName: "Route", decision: { condition: { source: "literal", operator: "equals", valueType: "boolean", value: true, compareTo: true, missingValue: "error" }, trueStepId: "summary", falseStepId: "summary" } } ] };
const prepared = { taskId: "task", revision: 2, name: "Captured task", inputs: { topic: "Current captured input" }, workflowFields: { model: "saved-model" }, provenance: { topic: { sourceTaskRunId: "producer", value: "bound" } }, timeoutSeconds: 90, prompt: "Prepared prompt", formatVersion: 1 };
const run = { id: runId, workflowId: id, kind: "Custom", status: "Running", definitionStepId: "summary", loopIteration: 2, executionAttemptId: currentAttempt, attemptCount: 2, externalId: "worker-current", customTaskExecution: prepared, output: null, agentMessages: [], createdAt: time, updatedAt: time, startedAt: time };
const archived = { id: "archive", workflowId: id, taskRunId: runId, executionAttemptId: priorAttempt, attemptNumber: 1, definitionStepId: "summary", loopIteration: 2, status: "Failed", externalId: "worker-previous", failureReason: "First attempt failed", customTaskExecutionJson: JSON.stringify({ ...prepared, inputs: { topic: "Old captured input" } }), structuredOutputsJson: '{"result":"historical"}', output: "Old output", createdAt: time, updatedAt: time, startedAt: time, completedAt: "2026-10-06T10:01:00Z" };
function execution() { return { workflow, definitionVersionId: "version-pinned", definition, runs: [run], attempts: [archived], loops: [], decisions: [], control: { isPaused: false, canPause: true, canResume: false, canCancel: true } }; }
function log(sequence: number, message = `Line ${sequence}`) { return { id: `log-${sequence}`, workflowId: id, taskRunId: runId, executionAttemptId: currentAttempt, source: "stdout", level: "Information", message, sequence, createdAt: time }; }
type Fixture = { snapshot?: () => unknown; logs?: (route: Route) => Promise<void>; stream?: (route: Route) => Promise<void>; search?: (route: Route) => Promise<void>; actions?: string[] };
async function fixture(page: Page, options: Fixture = {}) {
 await page.route("**/api/workflows**", async route => {
  const url = new URL(route.request().url()), path = url.pathname;
  if (path.endsWith("/execution")) return route.fulfill({ json: options.snapshot ? options.snapshot() : execution() });
  if (path.endsWith("/search")) return options.search ? options.search(route) : route.fulfill({ json: { items: [workflow], totalCount: 1, offset: 0, limit: 25 } });
  if (path.endsWith("/logs/page")) return options.logs ? options.logs(route) : route.fulfill({ json: { items: [log(10)], nextCursor: 10, previousCursor: 10, hasEarlier: true, hasMore: false } });
  if (path.endsWith("/logs/stream")) return options.stream ? options.stream(route) : route.fulfill({ contentType: "text/event-stream", body: "retry: 60000\n\n" });
  if (path.endsWith("/pause") || path.endsWith("/resume") || path.endsWith("/cancel") || path.endsWith("/retry")) { options.actions?.push(path.split("/").at(-1)!); return route.fulfill({ json: workflow }); }
  if (path === "/api/workflows") return route.fulfill({ json: [workflow] });
  return route.fulfill({ json: [] });
 });
}
test("pinned visual graph restores task iteration and archived attempt evidence through deep links", async ({ page }) => {
 await fixture(page); await page.goto(`/workflows?workflowId=${id}&node=summary&attempt=${priorAttempt}`);
 await expect(page.locator('.execution-graph .react-flow__node[data-id="summary"]')).toBeVisible();
 await expect(page.getByLabel("Iteration and attempt", { exact: true })).toHaveValue(priorAttempt);
 const inspector = page.getByRole("complementary", { name: "Task investigation" });
 await expect(inspector.getByText("Worker: worker-previous", { exact: true })).toBeVisible();
 await inspector.getByText("Prepared inputs", { exact: true }).click(); await expect(inspector.locator("pre").filter({ hasText: "Old captured input" })).toBeVisible();
 await inspector.getByText("Task output", { exact: true }).click(); await expect(inspector.locator("pre").filter({ hasText: "Old output" })).toBeVisible();
 await inspector.getByText("Pinned effective settings", { exact: true }).click(); await expect(inspector.locator("details").filter({ has: page.getByText("Pinned effective settings", { exact: true }) }).locator("pre")).toBeVisible();
 await page.getByLabel("Iteration and attempt", { exact: true }).selectOption(currentAttempt); await expect(page).toHaveURL(new RegExp(`attempt=${currentAttempt}`));
 await expect(inspector.getByText("Worker: worker-current", { exact: true })).toBeVisible();
 await expect(page.getByRole("link", { name: "Download JSON evidence" })).toHaveAttribute("href", `/api/workflows/${id}/evidence`);
});
test("native log stream replays reconnects deduplicates and displays readable worker events", async ({ page }, testInfo) => {
 const requests: string[] = []; let connections = 0;
 await fixture(page, { stream: async route => { requests.push((await route.request().allHeaders())["last-event-id"] ?? new URL(route.request().url()).searchParams.get("after") ?? ""); connections++; const entry = log(connections === 1 ? 11 : 12, JSON.stringify({ type: "item.completed", item: { type: "command_execution", command: "dotnet test", aggregated_output: connections === 1 ? "Tests started" : "Tests passed" } })); return route.fulfill({ contentType: "text/event-stream", body: `retry: 50\nid: ${entry.sequence}\nevent: log\ndata: ${JSON.stringify(entry)}\n\nid: ${entry.sequence}\nevent: log\ndata: ${JSON.stringify(entry)}\n\n` }); } });
 await page.goto(`/workflows?workflowId=${id}&node=summary`);
 await expect(page.getByRole("region", { name: "Worker logs" }).getByText("Tests passed", { exact: false }).first()).toBeVisible();
 const entries = page.locator(".execution-log-entry"); await expect(entries.filter({ hasText: "#11" })).toHaveCount(1); await expect(entries.filter({ hasText: "#12" })).toHaveCount(1);
 expect(requests[0]).toBe("10"); expect(requests.slice(1)).toContain("11");
 await expect(entries.filter({ hasText: "#11" }).locator("pre").first()).toHaveText("dotnet test\nTests started");
 await page.getByRole("button", { name: "Pause follow", exact: true }).click(); await expect(page.getByRole("region", { name: "Worker logs" }).getByRole("status")).toHaveText("Follow paused");
 await page.screenshot({ path: testInfo.outputPath("live-worker-investigation.png"), fullPage: true });
});
test("log scope filters older history and download retain server-side criteria", async ({ page }) => {
 const queries: URL[] = [];
 await fixture(page, { logs: async route => { const url = new URL(route.request().url()); queries.push(url); const earlier = url.searchParams.has("before"); return route.fulfill({ json: { items: [log(earlier ? 5 : 10)], nextCursor: earlier ? 5 : 10, previousCursor: earlier ? 5 : 10, hasEarlier: !earlier, hasMore: false } }); } });
 await page.goto(`/workflows?workflowId=${id}&node=summary&attempt=${priorAttempt}`);
 await page.getByLabel("Severity", { exact: true }).selectOption("Error"); await page.getByLabel("Stream", { exact: true }).selectOption("stderr"); await page.getByLabel("Search logs", { exact: true }).fill("timeout");
 await expect.poll(() => queries.at(-1)?.searchParams.get("search")).toBe("timeout");
 const href = await page.getByRole("link", { name: "Download filtered logs" }).getAttribute("href"), query = new URL(href!, "http://example.com").searchParams;
 expect(query.get("taskRunId")).toBe(runId); expect(query.get("executionAttemptId")).toBe(priorAttempt); expect(query.get("level")).toBe("Error"); expect(query.get("source")).toBe("stderr"); expect(query.get("search")).toBe("timeout");
 await page.getByRole("button", { name: "Load older logs", exact: true }).click(); await expect(page.locator(".execution-log-entry")).toHaveCount(2); expect(queries.at(-1)?.searchParams.get("before")).toBe("10");
 await expect(page.getByRole("region", { name: "Worker logs" }).getByRole("status")).toHaveText("Follow paused");
 await page.getByRole("button", { name: "Resume follow", exact: true }).click(); await expect.poll(() => queries.at(-1)?.searchParams.has("before")).toBe(false);
});
test("history pages and saved filters apply server search presets", async ({ page }) => {
 const queries: URL[] = [];
 await fixture(page, { search: async route => { const url = new URL(route.request().url()); queries.push(url); return route.fulfill({ json: { items: [workflow], totalCount: 51, offset: Number(url.searchParams.get("offset")), limit: 25 } }); } });
 await page.goto(`/workflows?workflowId=${id}`); await page.getByRole("button", { name: "Failed", exact: true }).click();
 await expect.poll(() => queries.at(-1)?.searchParams.get("status")).toBe("Failed"); await page.getByLabel("Search workflows", { exact: true }).fill("failure detail");
 await page.getByText("Filters and saved views", { exact: true }).click();
 await page.getByLabel("Saved view name", { exact: true }).fill("My failures"); await page.getByRole("button", { name: "Save current filters", exact: true }).click();
 await page.getByRole("button", { name: "Next workflows", exact: true }).click(); await expect.poll(() => queries.at(-1)?.searchParams.get("offset")).toBe("25");
 await page.getByRole("button", { name: "All runs", exact: true }).click(); await page.getByLabel("Saved filter views", { exact: true }).selectOption("My failures");
 await expect(page.getByLabel("Search workflows", { exact: true })).toHaveValue("failure detail"); await expect(page.getByLabel("Workflow status", { exact: true })).toHaveValue("Failed");
 await page.reload(); await expect(page.getByLabel("Saved filter views", { exact: true }).getByRole("option", { name: "My failures" })).toBeAttached();
});
test("execution refresh errors retain graph viewport selection and prior evidence", async ({ page }) => {
 let fail = false; await fixture(page); await page.route(`**/api/workflows/${id}/execution`, route => route.fulfill(fail ? { status: 503, json: { error: "Snapshot unavailable" } } : { json: execution() }));
 await page.goto(`/workflows?workflowId=${id}&node=summary&attempt=${priorAttempt}`); await expect(page.locator(".execution-graph .react-flow__node")).toHaveCount(2);
 await page.getByRole("button", { name: "Zoom Out", exact: true }).click(); const transform = await page.locator(".execution-graph .react-flow__viewport").getAttribute("style"); fail = true;
 await expect(page.getByRole("alert").filter({ hasText: "Snapshot unavailable" })).toBeVisible(); await expect(page.getByLabel("Iteration and attempt", { exact: true })).toHaveValue(priorAttempt);
 expect(await page.locator(".execution-graph .react-flow__viewport").getAttribute("style")).toBe(transform);
 await expect(page.getByText("Worker: worker-previous", { exact: true })).toBeVisible();
});
test("workflow controls explain scheduling pause and cancellation before changing state", async ({ page }) => {
 const actions: string[] = []; await fixture(page, { actions }); await page.goto(`/workflows?workflowId=${id}`);
 await page.getByRole("button", { name: "Pause scheduling", exact: true }).click(); const dialog = page.getByRole("dialog", { name: "Confirm workflow control" });
 await expect(dialog).toContainText("Active workers continue"); expect(actions).toEqual([]); await dialog.getByRole("button", { name: "Confirm", exact: true }).click(); await expect.poll(() => actions).toEqual(["pause"]);
 await page.getByRole("button", { name: "Cancel workflow", exact: true }).click(); await expect(dialog).toContainText("pending"); await dialog.getByRole("button", { name: "Keep current state", exact: true }).click(); expect(actions).toEqual(["pause"]);
});
test("node and attempt selections restore through browser back navigation", async ({ page }) => {
 await fixture(page); await page.goto(`/workflows?workflowId=${id}&node=summary&attempt=${priorAttempt}`);
 await page.getByLabel("Iteration and attempt", { exact: true }).selectOption(currentAttempt); await page.getByLabel("Task / node", { exact: true }).selectOption("route");
 await page.goBack(); await expect(page.getByLabel("Task / node", { exact: true })).toHaveValue("summary"); await expect(page.getByLabel("Iteration and attempt", { exact: true })).toHaveValue(currentAttempt);
 await page.goBack(); await expect(page.getByLabel("Iteration and attempt", { exact: true })).toHaveValue(priorAttempt);
});
test("failure navigation selects the failed attempt and retry requires explicit confirmation", async ({ page }) => {
 const actions: string[] = []; const failed = execution(); failed.workflow = { ...workflow, status: "Failed" }; failed.runs = [{ ...run, status: "Failed" }];
 await fixture(page, { snapshot: () => failed, actions }); await page.goto(`/workflows?workflowId=${id}`);
 await page.getByRole("button", { name: "Next failed task", exact: true }).click(); await expect(page.getByLabel("Iteration and attempt", { exact: true })).toHaveValue(currentAttempt);
 await page.getByRole("button", { name: "Retry selected task", exact: true }).click(); const dialog = page.getByRole("dialog", { name: "Confirm workflow control" }); await expect(dialog).toContainText("External side effects may run again"); expect(actions).toEqual([]);
 await dialog.getByRole("button", { name: "Confirm", exact: true }).click(); await expect.poll(() => actions).toEqual(["retry"]);
 await expect(page.locator('.execution-graph .react-flow__node[data-id="route"]')).toContainText("Not executed");
});
test("viewer can investigate evidence but cannot mutate workflow execution", async ({ page }) => {
 await fixture(page); await page.route("**/api/auth/current-user", async route => { const response = await route.fetch(); const user = await response.json(); return route.fulfill({ json: { ...user, canViewWorkflows: true, canTriggerWorkflows: false, canAdminister: false } }); });
 await page.goto(`/workflows?workflowId=${id}&node=summary`); await expect(page.getByRole("complementary", { name: "Task investigation" })).toBeVisible(); await expect(page.getByRole("link", { name: "Download JSON evidence" })).toBeVisible();
 await expect(page.getByRole("button", { name: "Pause scheduling", exact: true })).toHaveCount(0); await expect(page.getByRole("button", { name: "Cancel workflow", exact: true })).toHaveCount(0);
});
test("loop and parallel nodes show durable structural outcomes timing and branch entries", async ({ page }) => {
 const snapshot = { ...execution(), definition: { ...definition, steps: [ ...definition.steps, { id: "loop", uses: "builtins.loop", displayName: "Repeat", loop: { bodyStepId: "summary", repeatCount: 2, maxIterations: 3 } }, { id: "parallel", uses: "builtins.parallel", displayName: "Parallel plans", parallel: { branchStepIds: ["summary"] } } ], editor: { positions: { ...definition.editor.positions, loop: { x: 0, y: 240 }, parallel: { x: 360, y: 240 } } } }, loops: [{ id: "iteration", workflowId: id, loopId: "loop", iterationNumber: 2, outcome: "Failed", startedAt: time, completedAt: "2026-10-06T10:01:00Z", failureReason: "Iteration failed" }], parallels: [{ id: "parallel-group", workflowId: id, nodeId: "parallel", outcome: "Succeeded", startedAt: time, completedAt: "2026-10-06T10:01:00Z" }] };
 await fixture(page, { snapshot: () => snapshot }); await page.goto(`/workflows?workflowId=${id}&node=loop`);
 const loop = page.getByRole("region", { name: "Recorded loop iterations" }); await expect(loop).toContainText("Iteration 2"); await expect(loop).toContainText("Failed"); await expect(loop).toContainText("1m 0s");
 await page.getByLabel("Task / node", { exact: true }).selectOption("parallel"); const parallel = page.getByRole("region", { name: "Parallel execution" }); await expect(parallel).toContainText("Succeeded"); await expect(parallel).toContainText("summary: Running");
 await expect(page.locator('.execution-graph .react-flow__node[data-id="parallel"]')).toContainText("Succeeded"); await expect(page.getByText("Workflow events (0)", { exact: true })).toBeVisible();
});
test("unsaved graph positions finish slow initial layout while execution snapshots keep refreshing", async ({ page }) => {
 let snapshots = 0, layoutImports = 0;
 await page.route("**/*elk*", async route => {
  layoutImports++;
  await route.fulfill({ contentType: "application/javascript", body: `export default class ELK { async layout(graph) { await new Promise(resolve => setTimeout(resolve, 4500)); return { ...graph, children: graph.children.map((child, index) => ({ ...child, x: index * 300, y: 20 })) }; } }` });
 });
 const document = { schema: "formicae.workflow/v1alpha3", startStepId: "plan1", steps: [1, 2, 3, 4].map(number => ({ id: `plan${number}`, uses: "builtins.plan", displayName: `Plan ${number}`, nextStepId: number < 4 ? `plan${number + 1}` : null })) };
 await fixture(page, { snapshot: () => { snapshots++; return { ...execution(), definition: document }; } });
 await page.goto(`/workflows?workflowId=${id}`);
 await expect.poll(() => snapshots, { timeout: 15000 }).toBeGreaterThanOrEqual(3);
 expect(layoutImports).toBeGreaterThan(0);
 const nodes = page.locator(".execution-graph .react-flow__node"); await expect(nodes).toHaveCount(4);
 await expect.poll(async () => {
  const bounds = await page.locator(".execution-graph").boundingBox(); if (!bounds) return false;
  return nodes.evaluateAll((items, canvas) => items.every(item => { const rect = item.getBoundingClientRect(); return rect.left >= canvas.x - 1 && rect.right <= canvas.x + canvas.width + 1 && rect.top >= canvas.y - 1 && rect.bottom <= canvas.y + canvas.height + 1; }), bounds);
 }).toBe(true);
 const viewport = await page.locator(".execution-graph .react-flow__viewport").getAttribute("style");
 await expect.poll(() => snapshots, { timeout: 10000 }).toBeGreaterThanOrEqual(4);
 await expect(nodes).toHaveCount(4); expect(await page.locator(".execution-graph .react-flow__viewport").getAttribute("style")).toBe(viewport);
});
