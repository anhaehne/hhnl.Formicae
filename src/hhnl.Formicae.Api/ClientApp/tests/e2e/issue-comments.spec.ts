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

test("issue event ports and comment input bindings survive editing and saving", async ({ page, request }, testInfo) => {
  const name = `Issue comment bindings ${Date.now()}`;
  const definition = await (await request.post(`${api}/api/workflow-definitions`, { data: { name } })).json();
  const saved = await request.post(`${api}/api/workflow-definitions/${definition.id}/versions`, { data: {
    isEnabled: true, isDefault: false, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "", steps: [
      { id: "created", uses: "github.issue-created", displayName: "Issue created", nextStepId: "writer", event: { enabled: false, repositoryIds: [] } },
      { id: "writer", uses: "builtins.agent-task", displayName: "Write response", nextStepId: "comment", customTask: { taskId: "", inputs: {}, bindings: { issue: { stepId: "created", outputName: "issue" } }, definition: { promptTemplate: "Read {{input.issue}}", inputs: [{ name: "issue", valueType: "string", required: true }], outputs: [{ name: "response", valueType: "string", required: true }], runner: { kind: "agent", timeoutSeconds: 30 } } } },
      { id: "comment", uses: "github.add-issue-comment", displayName: "Add comment", issueComment: { inputs: {}, bindings: { issueId: { stepId: "created", outputName: "issueId" }, text: { stepId: "writer", outputName: "response" } } } }
    ] }
  } });
  expect(saved.ok(), await saved.text()).toBeTruthy();
  await open(page, name);
  await expect(page.locator('.react-flow__node[data-id="created"] [data-handleid="output:issue"]')).toHaveCount(1);
  await expect(page.locator('.react-flow__node[data-id="created"] [data-handleid="output:issueId"]')).toHaveCount(1);
  await inspect(page, "comment");
  await expect(page.getByRole("heading", { name: "Add issue comment", exact: true }).first()).toBeVisible();
  await expect(page.getByLabel("Source for issueId")).toHaveValue(JSON.stringify(["created", "issueId"]));
  await expect(page.getByLabel("Source for text")).toHaveValue(JSON.stringify(["writer", "response"]));
  await expect(page.getByLabel("Step persona", { exact: true })).toHaveCount(0);
  await expect(page.getByLabel("Step environment", { exact: true })).toHaveCount(0);
  await expect(page.getByRole("combobox", { name: "Step model", exact: true })).toHaveCount(0);
  await page.getByLabel("Source for text").selectOption("");
  await page.getByLabel("Provide text").check();
  await page.getByLabel("Value for text").fill("A multiline comment\nwith **Markdown** and ✓");
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  const latest = (await (await request.get(`${api}/api/workflow-definitions/${definition.id}`)).json()).versions[0].definition;
  const comment = latest.steps.find((step: { id: string }) => step.id === "comment");
  expect(comment.issueComment.bindings).toEqual({ issueId: { stepId: "created", outputName: "issueId" } });
  expect(comment.issueComment.inputs.text).toBe("A multiline comment\nwith **Markdown** and ✓");
  await page.reload(); await open(page, name); await inspect(page, "comment");
  await expect(page.getByLabel("Value for text")).toHaveValue(comment.issueComment.inputs.text);
  await expect(page.getByLabel("Source for issueId")).toHaveValue(JSON.stringify(["created", "issueId"]));
  await page.screenshot({ path: testInfo.outputPath("issue-comment-settings.png"), fullPage: true });
});

test("Add issue comment appears in the task catalog with editable literals", async ({ page, request }) => {
  const name = `Comment task catalog ${Date.now()}`;
  const definition = await (await request.post(`${api}/api/workflow-definitions`, { data: { name } })).json();
  const saved = await request.post(`${api}/api/workflow-definitions/${definition.id}/versions`, { data: {
    isEnabled: false, isDefault: false, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "manual", steps: [
      { id: "manual", uses: "builtins.start", nextStepId: "comment", event: { enabled: true } },
      { id: "comment", uses: "github.add-issue-comment", issueComment: { inputs: { issueId: 7, text: "Hello" } } }
    ] }
  } });
  expect(saved.ok(), await saved.text()).toBeTruthy();
  await open(page, name); await inspect(page, "comment");
  await expect(page.getByLabel("Task", { exact: true })).toHaveValue("github.add-issue-comment");
  await expect(page.getByLabel("Value for issueId")).toHaveValue("7");
  await page.getByLabel("Value for issueId").fill("19");
  await page.getByLabel("Value for text").fill("Updated comment");
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  const latest = (await (await request.get(`${api}/api/workflow-definitions/${definition.id}`)).json()).versions[0].definition;
  expect(latest.steps.find((step: { id: string }) => step.id === "comment").issueComment.inputs).toEqual({ issueId: 19, text: "Updated comment" });
});
