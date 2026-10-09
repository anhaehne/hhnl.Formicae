import { expect, test } from "@playwright/test";

const api = "http://127.0.0.1:5000";

test("End palette node has only an input and persists as a terminal node", async ({ page, request }, testInfo) => {
  const name = `End palette ${Date.now()}`;
  const item = await (await request.post(`${api}/api/workflow-definitions`, { data: { name } })).json();
  const version = await request.post(`${api}/api/workflow-definitions/${item.id}/versions`, { data: {
    isEnabled: true, isDefault: false, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "manual",
      steps: [{ id: "manual", uses: "builtins.start", nextStepId: "plan", event: { enabled: true } },
        { id: "plan", uses: "builtins.plan", displayName: "Planning" }] }
  } });
  expect(version.ok(), await version.text()).toBeTruthy();
  await page.goto("/workflow-definitions");
  await page.locator(".editor-workflow-name").click();
  await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name, exact: true }).click();
  await page.getByRole("button", { name: "+ Add Step", exact: true }).click();
  await page.getByRole("complementary", { name: "Add step menu" }).getByRole("button", { name: /End Complete the workflow/ }).click();
  await expect(page.getByRole("heading", { name: "End", exact: true })).toBeVisible();
  await expect(page.getByText("The first incoming route reaching End completes this workflow. All other running tasks and waiting events are stopped.")).toBeVisible();
  await expect(page.getByLabel("Next step", { exact: true })).toHaveCount(0);
  await expect(page.getByLabel("Step model", { exact: true })).toHaveCount(0);
  const node = page.locator('.react-flow__node').filter({ hasText: "Complete workflow · Stop other work" });
  await expect(node.locator('[data-handleid="input"]')).toHaveCount(1);
  await expect(node.locator('.source')).toHaveCount(0);
  const id = await node.getAttribute("data-id");
  await page.getByLabel("Find a node").fill("plan");
  await page.locator(".editor-search-results").getByRole("button", { name: /\(plan\)$/ }).click();
  await page.getByLabel("Next step", { exact: true }).selectOption(JSON.stringify([id, "input"]));
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.locator(".editor-save-status")).toHaveText("Saved");
  const saved = (await (await request.get(`${api}/api/workflow-definitions/${item.id}`)).json()).versions[0].definition;
  expect(saved.steps.find((step: { id: string }) => step.id === id).uses).toBe("builtins.end");
  expect(saved.steps.find((step: { id: string }) => step.id === id).nextStepId).toBeNull();
  await page.reload();
  await page.locator(".editor-workflow-name").click();
  await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name, exact: true }).click();
  await expect(page.locator('.react-flow__node').filter({ hasText: "Complete workflow · Stop other work" })).toHaveCount(1);
  await page.screenshot({ path: testInfo.outputPath("end-node.png"), fullPage: true });
});

test("End completes a running workflow before another parallel route starts its next task", async ({ page, request }) => {
  test.setTimeout(60_000);
  const item = await (await request.post(`${api}/api/workflow-definitions`, { data: { name: `End execution ${Date.now()}` } })).json();
  const response = await request.post(`${api}/api/workflow-definitions/${item.id}/versions`, { data: { isEnabled: true, isDefault: false, definition: {
    schema: "formicae.workflow/v1alpha3", startStepId: "fork", steps: [
      { id: "fork", uses: "builtins.script", script: { script: "printf start" }, nextStepId: "fast", nextStepIds: ["slow"] },
      { id: "fast", uses: "builtins.script", script: { script: "printf finished" }, nextStepId: "end" },
      { id: "slow", uses: "builtins.script", script: { script: "printf slow" }, nextStepId: "after-slow" },
      { id: "after-slow", uses: "builtins.script", script: { script: "printf unreachable" }, nextStepId: "end" },
      { id: "end", uses: "builtins.end", displayName: "End workflow" }
    ]
  } } });
  expect(response.ok(), await response.text()).toBeTruthy();
  const version = await response.json();
  const created = await request.post(`${api}/api/workflows/github-issue`, { data: {
    issueUrl: `https://github.com/example/repo/issues/${Date.now()}`, repositoryUrl: "https://github.com/example/repo", workflowDefinitionVersionId: version.id
  } });
  expect(created.ok(), await created.text()).toBeTruthy();
  const id = (await created.json()).workflowId;
  await expect.poll(async () => (await (await request.get(`${api}/api/workflows/${id}`)).json()).status, { timeout: 30_000 }).toBe(5);
  const runs = await (await request.get(`${api}/api/workflows/${id}/runs`)).json();
  expect(runs.some((run: { definitionStepId: string }) => run.definitionStepId === "after-slow")).toBeFalsy();
  await page.goto(`/workflows?workflowId=${id}&node=end`);
  await expect(page.locator('.execution-graph .react-flow__node[data-id="end"]')).toContainText("Succeeded");
  await expect(page.locator('.execution-graph .react-flow__node[data-id="after-slow"]')).toContainText("Not executed");
});
