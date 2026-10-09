import { expect, test } from "@playwright/test";
const api = "http://127.0.0.1:5000";

test("GitHub issue comment waits have normal connections and retain settings on save", async ({ page, request }, testInfo) => {
  const name = `Issue comment wait ${Date.now()}`;
  const definition = await (await request.post(`${api}/api/workflow-definitions`, { data: { name } })).json();
  const created = await request.post(`${api}/api/workflow-definitions/${definition.id}/versions`, { data: {
    isEnabled: true, isDefault: false, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "start", steps: [
      { id: "start", uses: "builtins.start", event: { enabled: true }, nextStepId: "wait" },
      { id: "wait", uses: "github.issue-commented", displayName: "Wait for review", wait: { issueNumber: 7 }, nextStepId: "after" },
      { id: "after", uses: "builtins.script", script: { script: "echo resumed", shell: "sh", workingDirectory: "workspace", timeoutSeconds: 300 } }
    ] }
  } });
  expect(created.ok(), await created.text()).toBeTruthy();
  const errors: string[] = []; page.on("pageerror", error => errors.push(error.message));
  await page.goto("/workflow-definitions");
  await page.locator(".editor-workflow-name").click();
  await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name, exact: true }).click();
  await page.getByLabel("Find a node").fill("wait");
  await page.locator(".editor-search-results").getByRole("button", { name: /\(wait\)$/ }).click();
  await expect(page.getByRole("heading", { name: "GitHub: Issue commented", exact: true })).toBeVisible();
  await expect(page.locator('.react-flow__node[data-id="wait"] [data-handleid="input"]')).toHaveCount(1);
  await expect(page.locator('.react-flow__node[data-id="wait"] [data-handleid="output:body"]')).toHaveCount(1);
  const controlInput = await page.locator('.react-flow__node[data-id="wait"] [data-handleid="input"]').boundingBox();
  const issueInput = await page.locator('.react-flow__node[data-id="wait"] [data-handleid="data:issueNumber"]').boundingBox();
  expect(Math.abs(controlInput!.y - issueInput!.y)).toBeGreaterThan(20);
  const controlOutput = await page.locator('.react-flow__node[data-id="wait"] [data-handleid="next"]').boundingBox();
  const bodyOutput = await page.locator('.react-flow__node[data-id="wait"] [data-handleid="output:body"]').boundingBox();
  expect(Math.abs(controlOutput!.y - bodyOutput!.y)).toBeGreaterThan(20);
  await expect(page.getByLabel("Step model", { exact: true })).toHaveCount(0);
  await expect(page.getByLabel("Step environment", { exact: true })).toHaveCount(0);
  await expect(page.getByLabel("Issue number", { exact: true })).toHaveValue("7");
  await page.getByLabel("Issue number", { exact: true }).fill("77");
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  const saved = await (await request.get(`${api}/api/workflow-definitions/${definition.id}`)).json();
  const wait = saved.versions[0].definition.steps.find((node: { id: string }) => node.id === "wait");
  expect(wait.wait.issueNumber).toBe(77); expect(wait.nextStepId).toBe("after"); expect(wait.event).toBeFalsy();
  expect(saved.versions[0].definition.steps.find((node: { id: string }) => node.id === "start").nextStepId).toBe("wait");
  await page.reload();
  await expect(page.locator(".editor-save-status")).toHaveText("Saved");
  await page.locator(".editor-workflow-name").click();
  await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name, exact: true }).click();
  await page.getByLabel("Find a node").fill("wait");
  await page.locator(".editor-search-results").getByRole("button", { name: /\(wait\)$/ }).click();
  await expect(page.getByLabel("Issue number", { exact: true })).toHaveValue("77");
  await page.screenshot({ path: testInfo.outputPath("issue-comment-wait.png"), fullPage: true });
  expect(errors).toEqual([]);
});
