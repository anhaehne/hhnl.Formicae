import { expect, test, type Page } from "@playwright/test";

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

test("issue label input bindings survive editing and saving", async ({ page, request }, testInfo) => {
  test.setTimeout(60_000);
  const name = `Issue labels bindings ${Date.now()}`;
  const definition = await (await request.post(`${api}/api/workflow-definitions`, { data: { name } })).json();
  const saved = await request.post(`${api}/api/workflow-definitions/${definition.id}/versions`, { data: {
    isEnabled: true, isDefault: false, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "", steps: [
      { id: "created", uses: "github.issue-created", displayName: "Issue created", nextStepId: "writer", event: { enabled: false, repositoryIds: [] } },
      { id: "writer", uses: "builtins.agent-task", displayName: "Write response", nextStepId: "labels", customTask: { taskId: "", inputs: {}, bindings: { issue: { stepId: "created", outputName: "issue" } }, definition: { promptTemplate: "Read {{input.issue}}", inputs: [{ name: "issue", valueType: "string", required: true }], outputs: [{ name: "response", valueType: "string", required: true }], runner: { kind: "agent", timeoutSeconds: 30 } } } },
      { id: "labels", uses: "github.add-issue-labels", displayName: "Add labels", issueLabels: { inputs: {}, bindings: { issueId: { stepId: "created", outputName: "issueId" }, labels: { stepId: "writer", outputName: "response" } } } }
    ] }
  } });
  expect(saved.ok(), await saved.text()).toBeTruthy();
  await open(page, name);
  await expect(page.locator('.react-flow__node[data-id="created"] [data-handleid="output:issue"]')).toHaveCount(1);
  await expect(page.locator('.react-flow__node[data-id="created"] [data-handleid="output:issueId"]')).toHaveCount(1);
  await expect(page.locator('.react-flow__node[data-id="created"] [data-handleid="output:title"]')).toHaveCount(1);
  await inspect(page, "labels");
  await expect(page.getByRole("heading", { name: "Add issue labels", exact: true }).first()).toBeVisible();
  await expect(page.getByLabel("Source for issueId")).toHaveValue(JSON.stringify(["created", "issueId"]));
  await expect(page.getByLabel("Source for labels")).toHaveValue(JSON.stringify(["writer", "response"]));
  await expect(page.getByLabel("Step persona", { exact: true })).toHaveCount(0);
  await expect(page.getByLabel("Step environment", { exact: true })).toHaveCount(0);
  await expect(page.getByRole("combobox", { name: "Step model", exact: true })).toHaveCount(0);
  await page.getByLabel("Source for labels").selectOption(JSON.stringify(["created", "title"]));
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  await page.reload(); await open(page, name); await inspect(page, "labels");
  await expect(page.getByLabel("Source for labels")).toHaveValue(JSON.stringify(["created", "title"]));
  await page.getByLabel("Source for labels").selectOption("");
  await page.getByLabel("Provide labels").check();
  await page.getByLabel("Value for labels").fill("bug\nneeds review");
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  const latest = (await (await request.get(`${api}/api/workflow-definitions/${definition.id}`)).json()).versions[0].definition;
  const labels = latest.steps.find((step: { id: string }) => step.id === "labels");
  expect(labels.issueLabels.bindings).toEqual({ issueId: { stepId: "created", outputName: "issueId" } });
  expect(labels.issueLabels.inputs.labels).toBe("bug\nneeds review");
  await page.reload(); await open(page, name); await inspect(page, "labels");
  await expect(page.getByLabel("Value for labels")).toHaveValue(labels.issueLabels.inputs.labels);
  await expect(page.getByLabel("Source for issueId")).toHaveValue(JSON.stringify(["created", "issueId"]));
  await page.screenshot({ path: testInfo.outputPath("issue-labels-settings.png"), fullPage: true });
});

test("Add issue labels appears in the task catalog with editable literals", async ({ page, request }) => {
  const name = `Labels task catalog ${Date.now()}`;
  const definition = await (await request.post(`${api}/api/workflow-definitions`, { data: { name } })).json();
  const saved = await request.post(`${api}/api/workflow-definitions/${definition.id}/versions`, { data: {
    isEnabled: false, isDefault: false, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "manual", steps: [
      { id: "manual", uses: "builtins.start", nextStepId: "labels", event: { enabled: true } },
      { id: "labels", uses: "github.add-issue-labels", issueLabels: { inputs: { issueId: 7, labels: "bug" } } }
    ] }
  } });
  expect(saved.ok(), await saved.text()).toBeTruthy();
  await open(page, name); await inspect(page, "labels");
  await expect(page.getByLabel("Task", { exact: true })).toHaveValue("github.add-issue-labels");
  await expect(page.getByLabel("Value for issueId")).toHaveValue("7");
  await page.getByLabel("Value for issueId").fill("19");
  await page.getByLabel("Value for labels").fill("bug\ntriage");
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  const latest = (await (await request.get(`${api}/api/workflow-definitions/${definition.id}`)).json()).versions[0].definition;
  expect(latest.steps.find((step: { id: string }) => step.id === "labels").issueLabels.inputs).toEqual({ issueId: 19, labels: "bug\ntriage" });
  await page.getByRole("button", { name: "+ Add Step", exact: true }).click();
  await page.getByRole("complementary", { name: "Add step menu" }).getByRole("button", { name: /GitHub: Add issue labels/ }).click();
  await expect(page.getByRole("heading", { name: "Add issue labels", exact: true }).first()).toBeVisible();
  await page.getByLabel("Provide issueId").check();
  await page.getByLabel("Value for issueId").fill("42");
  await page.getByLabel("Provide labels").check();
  await page.getByLabel("Value for labels").fill("bug\ntriage");
  await expect(page.getByLabel("Value for labels")).toHaveValue("bug\ntriage");
});
