import { expect, test, type Page, type APIRequestContext } from "@playwright/test";
const api = "http://127.0.0.1:5000";
async function seed(request: APIRequestContext) {
  const producer = await (await request.post(`${api}/api/custom-tasks`, { data: { name: `Variable producer ${Date.now()}`, promptTemplate: "Return ready", outputs: [{ name: "summary", valueType: "string", required: true }] } })).json();
  const consumer = await (await request.post(`${api}/api/custom-tasks`, { data: { name: `Variable consumer ${Date.now()}`, promptTemplate: "Use {{input.summary}}", inputs: [{ name: "summary", valueType: "string", required: true }] } })).json();
  const item = await (await request.post(`${api}/api/workflow-definitions`, { data: { name: `Variables ${Date.now()}` } })).json();
  const response = await request.post(`${api}/api/workflow-definitions/${item.id}/versions`, { data: { isEnabled: true, isDefault: false, definition: {
    schema: "formicae.workflow/v1alpha3", startStepId: "a", steps: [
      { id: "a", uses: "builtins.custom-task", displayName: "Producer A", nextStepId: "b", customTask: { taskId: producer.id } },
      { id: "b", uses: "builtins.custom-task", displayName: "Producer B", nextStepId: "consumer", customTask: { taskId: producer.id } },
      { id: "consumer", uses: "builtins.custom-task", displayName: "Consumer", customTask: { taskId: consumer.id, bindings: { summary: { stepId: "combined", outputName: "value" } } } }
    ], variables: [{ id: "combined", name: "Combined", valueType: "string", mode: "aggregate", separator: " | ", booleanOperation: "any", sources: [{ stepId: "b", outputName: "summary" }, { stepId: "a", outputName: "summary" }] }],
    editor: { positions: { a: { x: 50, y: 50 }, b: { x: 400, y: 50 }, combined: { x: 400, y: 350 }, consumer: { x: 750, y: 50 } } }
  } } });
  expect(response.ok(), await response.text()).toBeTruthy();
  return { item, version: await response.json() };
}
async function inspect(page: Page, id: string) {
  await page.getByLabel("Find a node").fill(id);
  await page.locator(".editor-search-results").getByRole("button", { name: new RegExp(`\\(${id}\\)$`) }).click();
}
async function open(page: Page, name: string) {
  await page.goto("/workflow-definitions"); await page.locator(".editor-workflow-name").click();
  await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name, exact: true }).click();
  await expect(page.locator(".editor-save-status")).toHaveText("Saved");
}

test("variables preserve multiple sources ordering modes duplication and typed ports", async ({ page, request }, testInfo) => {
  const { item } = await seed(request); await open(page, item.name); await inspect(page, "combined");
  const capsule = page.locator('.react-flow__node[data-id="combined"]');
  await expect(capsule.locator(".editor-node.variable")).toBeVisible();
  await expect(capsule.locator('[data-handleid="input"], [data-handleid="next"]')).toHaveCount(0);
  await expect(page.locator('.react-flow__edge').filter({ has: page.locator('path') })).not.toHaveCount(0);
  await expect(page.locator('.react-flow__edge[data-id^="data:"][data-id$=":combined:value"]')).toHaveCount(2);
  await expect(page.locator(".variable-sources li").first()).toContainText("Producer B");
  await page.getByLabel("Move source 2 up").click(); await expect(page.locator(".variable-sources li").first()).toContainText("Producer A");
  await page.getByLabel("Variable combination").selectOption("first"); await page.getByLabel("Variable combination").blur();
  await page.getByRole("button", { name: "Undo", exact: true }).click(); await expect(page.getByLabel("Variable combination")).toHaveValue("aggregate");
  await page.getByRole("button", { name: "Redo", exact: true }).click(); await expect(page.getByLabel("Variable combination")).toHaveValue("first");
  await page.getByLabel("Variable combination").selectOption("override");
  await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.locator(".editor-save-status")).toHaveText("Saved");
  const saved = (await (await request.get(`${api}/api/workflow-definitions/${item.id}`)).json()).versions[0].definition;
  expect(saved.variables[0].sources.map((source: { stepId: string }) => source.stepId)).toEqual(["a", "b"]);
  expect(saved.variables[0].mode).toBe("override"); expect(saved.steps.filter((step: { uses: string }) => step.uses !== "builtins.start")).toHaveLength(3);
  expect(saved.steps.some((step: { id: string }) => step.id === "combined")).toBeFalsy();
  await page.reload(); await open(page, item.name); await inspect(page, "combined");
  await expect(page.getByLabel("Variable combination")).toHaveValue("override");
  await page.getByRole("button", { name: "Duplicate task", exact: true }).click();
  await expect(page.locator('.react-flow__node[data-id="combined-copy"] .editor-node.variable')).toBeVisible();
  await expect(page.locator(".variable-sources li")).toHaveCount(2);
  await page.getByLabel("Remove source 1").click(); await expect(page.locator(".variable-sources li")).toHaveCount(1);
  await page.getByLabel("Variable type").selectOption("boolean"); await page.getByLabel("Variable combination").selectOption("aggregate");
  await page.getByLabel("Boolean operation").selectOption("all"); await expect(page.getByLabel("Variable source").locator("option")).toHaveCount(1);
  await expect(page.getByRole("complementary", { name: "Variable inspector" }).getByRole("alert").first()).toBeVisible();
  await page.getByLabel("Remove source 1").click();
  await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.locator(".editor-save-status")).toHaveText("Saved");
  await page.screenshot({ path: testInfo.outputPath("variable-settings.png"), fullPage: true });
});

test("variables resolve in a real workflow and expose frozen evidence without task runs", async ({ page, request }, testInfo) => {
  const { version } = await seed(request);
  const response = await request.post(`${api}/api/workflows/github-issue`, { data: { issueUrl: `https://github.com/example/repo/issues/${Date.now()}`, repositoryUrl: "https://github.com/example/repo", workflowDefinitionVersionId: version.id } });
  expect(response.ok(), await response.text()).toBeTruthy(); const workflow = await response.json();
  await expect.poll(async () => (await (await request.get(`${api}/api/workflows/${workflow.workflowId}/runs`)).json()).filter((run: { status: string | number }) => run.status === "Succeeded" || run.status === 2).length, { timeout: 30_000 }).toBe(3);
  const runs = await (await request.get(`${api}/api/workflows/${workflow.workflowId}/runs`)).json();
  expect(runs.some((run: { definitionStepId: string }) => run.definitionStepId === "combined")).toBeFalsy();
  const prepared = runs.find((run: { definitionStepId: string }) => run.definitionStepId === "consumer").customTaskExecution;
  expect(prepared.inputs.summary).toBe("ready | ready");
  expect(prepared.provenance.summary.variable.sources.map((source: { stepId: string }) => source.stepId)).toEqual(["b", "a"]);
  await page.goto(`/workflows?workflowId=${workflow.workflowId}&node=combined`);
  const evidence = page.getByRole("region", { name: "Variable evidence" }); await expect(evidence).toBeVisible();
  await evidence.getByText("Frozen values and source provenance", { exact: true }).click(); await expect(evidence.locator("pre").last()).toContainText("ready | ready");
  await expect(page.locator('.react-flow__node[data-id="combined"] .execution-node-state')).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath("variable-execution.png"), fullPage: true });
});
