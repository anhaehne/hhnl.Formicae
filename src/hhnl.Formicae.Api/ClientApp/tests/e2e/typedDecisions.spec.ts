import { expect, test, type Page } from "@playwright/test";
const api = "http://127.0.0.1:5000";
async function find(page: Page, id: string) {
  await page.getByLabel("Find a node").fill(id);
  await page.locator(".editor-search-results").getByRole("button", { name: new RegExp(`\\(${id}\\)$`) }).click();
}
for (const type of ["string", "number"] as const) test(`${type} decision infers ports, requires default and preserves cases`, async ({ page, request }, testInfo) => {
  test.setTimeout(60000);
  const name = `Typed ${type} ${Date.now()}`;
  const taskResponse = await request.post(`${api}/api/custom-tasks`, { data: { name: `${name} producer`, promptTemplate: "Return outputs", outputs: [{ name: "value", valueType: type, required: true }, { name: "flag", valueType: "boolean", required: true }] } });
  expect(taskResponse.ok()).toBeTruthy(); const task = await taskResponse.json();
  const item = await (await request.post(`${api}/api/workflow-definitions`, { data: { name } })).json();
  const response = await request.post(`${api}/api/workflow-definitions/${item.id}/versions`, { data: { isEnabled: false, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "source", steps: [
    { id: "source", uses: "builtins.custom-task", displayName: "Producer", nextStepId: "choose", customTask: { taskId: task.id, inputs: {} } },
    { id: "choose", uses: "builtins.decision", displayName: "Choose", decision: { inputType: "any", condition: { source: "literal", valueType: "boolean", operator: "equals", value: true, compareTo: true, missingValue: "error" }, trueStepId: "", falseStepId: "", cases: [] } },
    { id: "yes", uses: "builtins.end", displayName: "Matched" }, { id: "no", uses: "builtins.end", displayName: "Default end" }
  ] } } }); expect(response.ok()).toBeTruthy();
  await page.goto("/workflow-definitions"); await page.locator(".editor-workflow-name").click();
  await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name, exact: true }).click();
  await find(page, "choose");
  await page.getByLabel("Decision input source").selectOption(JSON.stringify(["source", "value"]));
  await expect(page.locator('[data-id="choose"] [data-handleid="data:value"]')).toHaveAttribute("title", `value: ${type}`);
  await page.getByRole("button", { name: "Add case", exact: true }).click();
  await page.getByLabel("Value for case 1").fill(type === "string" ? "ready" : "3");
  if (type === "number") await page.getByLabel("Comparison for case 1").selectOption("greaterThan");
  await page.getByLabel(type === "string" ? '"ready" route' : "> 3 route", { exact: true }).selectOption(JSON.stringify(["yes", "input"]));
  await page.getByRole("button", { name: "Workflow settings", exact: true }).click(); await page.getByLabel("Enabled", { exact: true }).check();
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.getByText(/Default exit|Decision default/).first()).toBeVisible();
  await find(page, "choose"); await page.getByLabel("Default route", { exact: true }).selectOption(JSON.stringify(["no", "input"]));
  await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.locator(".editor-save-status")).toHaveText("Saved");
  const saved = (await (await request.get(`${api}/api/workflow-definitions/${item.id}`)).json()).versions[0].definition;
  expect(saved.steps.find((step: { id: string }) => step.id === "choose").decision).toMatchObject({ inputType: type, inputBinding: { stepId: "source", outputName: "value" }, cases: [{ value: type === "string" ? "ready" : 3, operator: type === "string" ? "equals" : "greaterThan", stepId: "yes" }], defaultStepId: "no" });
  await page.reload(); await page.locator(".editor-workflow-name").click(); await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name, exact: true }).click(); await find(page, "choose");
  await expect(page.getByLabel("Value for case 1")).toHaveValue(type === "string" ? "ready" : "3");
  await page.screenshot({ path: testInfo.outputPath(`${type}-decision.png`) });
  await page.getByLabel("Decision input source").selectOption(JSON.stringify(["source", "flag"])); await page.getByRole("dialog").getByRole("button", { name: "Replace", exact: true }).click();
  await expect(page.getByLabel("True route", { exact: true })).toBeVisible(); await expect(page.getByLabel("Default route", { exact: true })).toHaveCount(0);
  await expect(page.locator('[data-id="choose"] [data-handleid^="case:"]')).toHaveCount(0);
  await page.getByRole("button", { name: "Undo", exact: true }).click(); await expect(page.getByLabel("Default route", { exact: true })).toHaveValue(JSON.stringify(["no", "input"]));
});

test("custom task catalog entries add named nodes with their typed schema", async ({ page, request }) => {
  const name = `Named task ${Date.now()}`;
  const task = await (await request.post(`${api}/api/custom-tasks`, { data: { name, promptTemplate: "Return result", inputs: [{ name: "input", valueType: "string" }], outputs: [{ name: "result", valueType: "boolean" }] } })).json();
  await page.goto("/workflow-definitions"); await page.getByRole("button", { name: "+ Add Step", exact: true }).click();
  await page.getByLabel("Search step types").fill(name);
  await page.getByRole("complementary", { name: "Add step menu" }).getByRole("button", { name: new RegExp(name) }).click();
  await expect(page.getByLabel("Display Name", { exact: true })).toHaveValue(name);
  await expect(page.getByLabel("Task definition", { exact: true })).toHaveValue(task.id);
  await expect(page.locator(`[title="result: boolean"]`).first()).toBeVisible();
  await expect(page.getByLabel("Decision input source")).toHaveCount(0);
});
