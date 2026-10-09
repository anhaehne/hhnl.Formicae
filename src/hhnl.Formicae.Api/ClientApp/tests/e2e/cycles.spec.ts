import { expect, test, type Page } from "@playwright/test";
const api = "http://127.0.0.1:5000";
async function open(page: Page, name: string) {
  await page.goto("/workflow-definitions"); await page.locator(".editor-workflow-name").click();
  await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name, exact: true }).click();
  await expect(page.locator(".editor-save-status")).toHaveText("Saved");
  await page.getByRole("button", { name: "Fit All", exact: true }).click();
}
async function drag(page: Page, sourceId: string, sourcePort: string, targetId: string, targetPort: string) {
  const source = await page.locator(`.react-flow__node[data-id="${sourceId}"] [data-handleid="${sourcePort}"]`).boundingBox();
  const target = await page.locator(`.react-flow__node[data-id="${targetId}"] [data-handleid="${targetPort}"]`).boundingBox();
  expect(source).toBeTruthy(); expect(target).toBeTruthy();
  await page.mouse.move(source!.x + source!.width / 2, source!.y + source!.height / 2); await page.mouse.down();
  await page.mouse.move(target!.x + target!.width / 2, target!.y + target!.height / 2, { steps: 20 }); await page.mouse.up();
}

test("native comment body connections retain multiple sources and typed hover labels", async ({ page, request }, testInfo) => {
  const item = await (await request.post(`${api}/api/workflow-definitions`, { data: { name: `Comment variable ports ${Date.now()}` } })).json();
  const response = await request.post(`${api}/api/workflow-definitions/${item.id}/versions`, { data: { isEnabled: true, isDefault: false, definition: {
    schema: "formicae.workflow/v1alpha3", startStepId: "a", steps: [
      { id: "a", uses: "github.issue-commented", displayName: "Comment A", nextStepId: "b", wait: { issueNumber: 7 } },
      { id: "b", uses: "github.issue-commented", displayName: "Comment B", wait: { issueNumber: 7 } }
    ], variables: [{ id: "combined", name: "Combined", valueType: "string", mode: "aggregate", separator: " | ", sources: [] },
      { id: "number", name: "Number", valueType: "number", mode: "aggregate", sources: [] }],
    editor: { groups: [{ id: "data", name: "Data", nodeIds: ["combined", "number"] }], positions: { a: { x: 0, y: 0 }, b: { x: 350, y: 0 }, combined: { x: 300, y: 380 }, number: { x: 650, y: 380 } } }
  } } }); expect(response.ok(), await response.text()).toBeTruthy();
  await open(page, item.name);
  const port = page.locator('.react-flow__node[data-id="a"] [data-handleid="output:body"]');
  await expect(port).toHaveAttribute("title", "body: string"); await expect(port).toHaveAttribute("aria-label", /string/);
  await drag(page, "a", "output:body", "combined", "data:value");
  await drag(page, "b", "output:body", "combined", "data:value");
  await expect(page.locator('.react-flow__edge[data-id^="data:"][data-id$=":combined:value"]')).toHaveCount(2);
  await drag(page, "a", "output:author", "combined", "data:value");
  await expect(page.locator('.react-flow__edge[data-id^="data:"][data-id$=":combined:value"]')).toHaveCount(3);
  await page.getByRole("button", { name: "Undo", exact: true }).click();
  await expect(page.locator('.react-flow__edge[data-id^="data:"][data-id$=":combined:value"]')).toHaveCount(2);
  await page.getByRole("button", { name: "Redo", exact: true }).click();
  await expect(page.locator('.react-flow__edge[data-id^="data:"][data-id$=":combined:value"]')).toHaveCount(3);
  await drag(page, "a", "output:body", "number", "data:value");
  await expect(page.getByText(/string.*number|number.*string/).filter({ hasText: /connect|type|accept/i }).first()).toBeVisible();
  await expect(page.locator('.react-flow__edge[data-id^="data:"][data-id$=":number:value"]')).toHaveCount(0);
  await drag(page, "b", "next", "a", "input");
  await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.locator(".editor-save-status")).toHaveText("Saved");
  await page.reload(); await open(page, item.name);
  const saved = (await (await request.get(`${api}/api/workflow-definitions/${item.id}`)).json()).versions[0].definition;
  expect(saved.variables.find((v: { id: string }) => v.id === "combined").sources.map((v: { stepId: string }) => v.stepId)).toEqual(["a", "b", "a"]);
  expect(saved.steps.find((s: { id: string }) => s.id === "b").nextStepId).toBe("a");
  expect(saved.editor.groups[0].nodeIds).toEqual(["combined", "number"]);
  await page.screenshot({ path: testInfo.outputPath("comment-body-cycle.png"), fullPage: true });
});

test("feedback variable accepts later comment outputs when it feeds an earlier task", async ({ page, request }) => {
  const task = await (await request.post(`${api}/api/custom-tasks`, { data: { name: `Feedback consumer ${Date.now()}`, promptTemplate: "Use {{input.text}}", inputs: [{ name: "text", valueType: "string", required: false, defaultValue: "initial" }] } })).json();
  const item = await (await request.post(`${api}/api/workflow-definitions`, { data: { name: `Feedback ports ${Date.now()}` } })).json();
  const response = await request.post(`${api}/api/workflow-definitions/${item.id}/versions`, { data: { isEnabled: true, isDefault: false, definition: {
    schema: "formicae.workflow/v1alpha3", startStepId: "consumer", steps: [
      { id: "consumer", uses: "builtins.custom-task", customTask: { taskId: task.id, bindings: { text: { stepId: "combined", outputName: "value" } } }, nextStepId: "a" },
      { id: "a", uses: "github.issue-commented", wait: { issueNumber: 7 }, nextStepId: "b" },
      { id: "b", uses: "github.issue-commented", wait: { issueNumber: 7 }, nextStepId: "consumer" }
    ], variables: [{ id: "combined", name: "Feedback", valueType: "string", mode: "aggregate", sources: [] }],
    editor: { positions: { consumer: { x: 0, y: 0 }, a: { x: 350, y: 0 }, b: { x: 700, y: 0 }, combined: { x: 500, y: 380 } } }
  } } }); expect(response.ok(), await response.text()).toBeTruthy(); await open(page, item.name);
  await drag(page, "a", "output:body", "combined", "data:value"); await drag(page, "b", "output:body", "combined", "data:value");
  await expect(page.locator('.react-flow__edge[data-id^="data:"][data-id$=":combined:value"]')).toHaveCount(2);
  await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.locator(".editor-save-status")).toHaveText("Saved");
});

test("unbounded self cycle creates fresh visits and can pause resume and cancel", async ({ page, request }) => {
  test.setTimeout(60_000);
  const item = await (await request.post(`${api}/api/workflow-definitions`, { data: { name: `Unbounded visits ${Date.now()}` } })).json();
  const response = await request.post(`${api}/api/workflow-definitions/${item.id}/versions`, { data: { isEnabled: true, isDefault: false, definition: {
    schema: "formicae.workflow/v1alpha3", startStepId: "repeat", steps: [{ id: "repeat", uses: "builtins.script", nextStepId: "repeat", script: { script: "printf visit", shell: "sh", timeoutSeconds: 30, workingDirectory: "workspace" } }]
  } } }); expect(response.ok(), await response.text()).toBeTruthy(); const version = await response.json();
  const created = await request.post(`${api}/api/workflows/github-issue`, { data: { issueUrl: `https://github.com/example/repo/issues/${Date.now()}`, repositoryUrl: "https://github.com/example/repo", workflowDefinitionVersionId: version.id } });
  expect(created.ok(), await created.text()).toBeTruthy(); const id = (await created.json()).workflowId;
  const runs = async () => (await (await request.get(`${api}/api/workflows/${id}/runs`)).json()) as { loopIteration: number; executionAttemptId: string }[];
  try {
    await expect.poll(async () => (await runs()).length, { timeout: 25_000 }).toBeGreaterThanOrEqual(4);
    expect((await request.post(`${api}/api/workflows/${id}/pause`)).ok()).toBeTruthy();
    const paused = await runs(); await page.goto(`/workflows?workflowId=${id}&node=repeat`);
    await expect(page.getByLabel("Iteration and attempt").locator("option").first()).toContainText("visit");
    await page.waitForTimeout(1_500); expect((await runs()).length).toBe(paused.length);
    expect(new Set(paused.map(run => run.loopIteration)).size).toBe(paused.length);
    expect(new Set(paused.map(run => run.executionAttemptId)).size).toBe(paused.length);
    expect((await request.post(`${api}/api/workflows/${id}/resume`)).ok()).toBeTruthy();
    await expect.poll(async () => (await runs()).length, { timeout: 10_000 }).toBeGreaterThan(paused.length);
  } finally { expect((await request.post(`${api}/api/workflows/${id}/cancel`)).ok()).toBeTruthy(); }
  await expect.poll(async () => ["Canceled", 7].includes((await (await request.get(`${api}/api/workflows/${id}`)).json()).status), { timeout: 10_000 }).toBeTruthy();
});
