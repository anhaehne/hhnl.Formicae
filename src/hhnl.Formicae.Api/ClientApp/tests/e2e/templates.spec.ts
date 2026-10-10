import { expect, test, type Page } from "@playwright/test";
test.setTimeout(90_000);
const api = "http://127.0.0.1:5000";
async function open(page: Page, name: string) {
  await page.goto("/workflow-definitions");
  await page.locator(".editor-workflow-name").click();
  await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name, exact: true }).click();
  await expect(page.locator(".editor-save-status")).toHaveText("Saved");
}
async function inspect(page: Page, id: string) {
  await page.getByLabel("Find a node").fill(id);
  await page.locator(".editor-search-results").getByRole("button", { name: new RegExp(`\\(${id}\\)$`) }).click();
}

test("Template textarea, named typed bindings and text output survive save and reload", async ({ page, request }, testInfo) => {
  const name = `Template bindings ${Date.now()}`;
  const definition = await (await request.post(`${api}/api/workflow-definitions`, { data: { name } })).json();
  const saved = await request.post(`${api}/api/workflow-definitions/${definition.id}/versions`, { data: { isEnabled: true, isDefault: false, definition: {
    schema: "formicae.workflow/v1alpha3", startStepId: "", steps: [
      { id: "created", uses: "github.issue-created", nextStepId: "template", event: { enabled: false, repositoryIds: [] } },
      { id: "template", uses: "builtins.template", displayName: "Template", nextStepId: "comment", template: { template: "Issue {{ inputs.id }}\n{{ inputs.issue }}", inputs: [{ name: "id", valueType: "number", required: true }, { name: "issue", valueType: "string", required: true }], bindings: { id: { stepId: "created", outputName: "issueId" }, issue: { stepId: "created", outputName: "title" } } } },
      { id: "comment", uses: "github.add-issue-comment", issueComment: { inputs: {}, bindings: { issueId: { stepId: "created", outputName: "issueId" }, text: { stepId: "template", outputName: "text" } } } }
    ] } } });
  expect(saved.ok(), await saved.text()).toBeTruthy();
  await open(page, name); await inspect(page, "template");
  await expect(page.getByLabel("Scriban template")).toHaveJSProperty("tagName", "TEXTAREA");
  await expect(page.getByLabel("Source for id")).toHaveValue(JSON.stringify(["created", "issueId"]));
  await expect(page.getByLabel("Source for issue")).toHaveValue(JSON.stringify(["created", "title"]));
  await expect(page.locator('.react-flow__node[data-id="template"] [data-handleid="output:text"]')).toHaveCount(1);
  await expect(page.getByLabel("Step environment", { exact: true })).toHaveCount(0);
  const text = "# Issue {{ inputs.id }}\n{{ inputs.details | string.upcase }}";
  await page.getByLabel("Template input 2 name").fill("details");
  await expect(page.getByLabel("Source for details")).toHaveValue(JSON.stringify(["created", "title"]));
  await page.getByLabel("Scriban template").fill(text);
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  const latest = (await (await request.get(`${api}/api/workflow-definitions/${definition.id}`)).json()).versions[0].definition;
  expect(latest.steps.find((step: { id: string }) => step.id === "template").template).toMatchObject({ template: text, bindings: { id: { stepId: "created", outputName: "issueId" }, details: { stepId: "created", outputName: "title" } } });
  await page.reload(); await open(page, name); await inspect(page, "template");
  await expect(page.getByLabel("Scriban template")).toHaveValue(text);
  await page.screenshot({ path: testInfo.outputPath("template-settings.png"), fullPage: true });
  await page.getByRole("button", { name: "Remove input 2", exact: true }).click();
  await expect(page.locator('.react-flow__node[data-id="template"] [data-handleid="data:details"]')).toHaveCount(0);
  await page.getByLabel("Scriban template").fill("Issue {{ inputs.id }}");
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  const reduced = (await (await request.get(`${api}/api/workflow-definitions/${definition.id}`)).json()).versions[0].definition;
  expect(reduced.steps.find((step: { id: string }) => step.id === "template").template.bindings).toEqual({ id: { stepId: "created", outputName: "issueId" } });
});

test("Template catalog allows more than 32 inputs and type changes disconnect stale sources", async ({ page, request }) => {
  const name = `Template input count ${Date.now()}`;
  const definition = await (await request.post(`${api}/api/workflow-definitions`, { data: { name } })).json();
  const saved = await request.post(`${api}/api/workflow-definitions/${definition.id}/versions`, { data: { isEnabled: false, isDefault: false, definition: {
    schema: "formicae.workflow/v1alpha3", startStepId: "start", steps: [
      { id: "start", uses: "builtins.start", nextStepId: "source", event: { enabled: true } },
      { id: "source", uses: "builtins.template", template: { template: "Source", inputs: [] } }
    ] } } });
  expect(saved.ok(), await saved.text()).toBeTruthy();
  await open(page, name);
  await page.getByRole("button", { name: "+ Add Step", exact: true }).click();
  await page.getByRole("complementary", { name: "Add step menu" }).getByRole("button", { name: /Template Render text/ }).click();
  const newId = await page.locator('.react-flow__node.selected').getAttribute("data-id");
  expect(newId).toBeTruthy();
  await page.getByLabel("Next step", { exact: true }).selectOption(JSON.stringify(["source", "input"]));
  await page.getByRole("button", { name: "Add template input", exact: true }).click();
  // Source is downstream and must be excluded from the selector.
  await expect(page.getByLabel("Source for input1").locator("option", { hasText: "Source → text" })).toHaveCount(0);
  await inspect(page, "source");
  await page.getByLabel("Next step", { exact: true }).selectOption(JSON.stringify([newId, "input"]));
  await inspect(page, newId!);
  await page.getByLabel("Next step", { exact: true }).selectOption("");
  await page.getByLabel("Source for input1").selectOption(JSON.stringify(["source", "text"]));
  await page.getByLabel("Template input 1 type").selectOption("boolean");
  await expect(page.getByLabel("Source for input1")).toHaveValue("");
  for (let i = 1; i < 33; i++) await page.getByRole("button", { name: "Add template input", exact: true }).click();
  await expect(page.getByLabel("Template input 33 name")).toHaveValue("input33");
  await expect(page.getByRole("button", { name: "Add template input", exact: true })).toBeEnabled();
});

test("Template runs inline and exposes rendered text and provenance in execution history", async ({ page, request }, testInfo) => {
  const definition = await (await request.post(`${api}/api/workflow-definitions`, { data: { name: `Template execution ${Date.now()}` } })).json();
  const saved = await request.post(`${api}/api/workflow-definitions/${definition.id}/versions`, { data: { isEnabled: true, isDefault: false, definition: {
    schema: "formicae.workflow/v1alpha3", startStepId: "source", steps: [
      { id: "source", uses: "builtins.template", nextStepId: "render", template: { template: "World ✓", inputs: [] } },
      { id: "render", uses: "builtins.template", displayName: "Rendered greeting", template: { template: "Hello {{ inputs.name }}!\nPlain <text>", inputs: [{ name: "name", valueType: "string", required: true }], bindings: { name: { stepId: "source", outputName: "text" } } } }
    ] } } });
  expect(saved.ok(), await saved.text()).toBeTruthy();
  const version = await saved.json();
  const started = await request.post(`${api}/api/workflows/github-issue`, { data: { issueUrl: `https://github.com/example/repo/issues/${Date.now()}`, repositoryUrl: "https://github.com/example/repo", workflowDefinitionVersionId: version.id } });
  expect(started.ok(), await started.text()).toBeTruthy();
  const id = (await started.json()).workflowId;
  await expect.poll(async () => (await (await request.get(`${api}/api/workflows/${id}`)).json()).status, { timeout: 30_000 }).toBe(5);
  const runs = await (await request.get(`${api}/api/workflows/${id}/runs`)).json();
  const render = runs.find((run: { definitionStepId: string }) => run.definitionStepId === "render");
  expect(render.structuredOutputs.text).toBe("Hello World ✓!\nPlain <text>");
  expect(render.externalId).toBeNull();
  expect(render.templateExecution.provenance.name.stepId).toBe("source");
  await page.goto(`/workflows?workflowId=${id}&node=render`);
  const evidence = page.getByRole("region", { name: "Template execution" });
  await expect(evidence).toBeVisible();
  await evidence.getByText("Prepared inputs", { exact: true }).click();
  await expect(evidence).toContainText("World ✓");
  await page.getByText("Task output", { exact: true }).click();
  await expect(page.locator(".execution-inspector")).toContainText("Hello World ✓!\nPlain <text>");
  await page.screenshot({ path: testInfo.outputPath("template-execution.png"), fullPage: true });
});
