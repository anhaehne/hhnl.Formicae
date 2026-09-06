import { expect, test, type Page, type APIRequestContext } from "@playwright/test";
const api = "http://127.0.0.1:5000";
async function seed(request: APIRequestContext, count = 3) {
  const name = `Editor ${Date.now()}-${Math.random().toString(16).slice(2)}`;
  const definition = await (await request.post(`${api}/api/workflow-definitions`, { data: { name } })).json();
  const response = await request.post(`${api}/api/workflow-definitions/${definition.id}/versions`, { data: { isEnabled: true, isDefault: false,
    definition: { schema: "formicae.workflow/v1alpha3", startStepId: "n0", steps: Array.from({ length: count }, (_, i) => ({ id: `n${i}`, uses: "builtins.plan", displayName: `Task ${i}`, nextStepId: i + 1 < count ? `n${i+1}` : null })) } } });
  expect(response.ok()).toBeTruthy();
  return { ...definition, version: await response.json() };
}
async function open(page: Page, name: string) {
  await page.goto("/workflow-definitions");
  await page.locator(".editor-workflow-name").click();
  await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name, exact: true }).click();
  await expect(page.locator(".editor-save-status")).toHaveText("Saved");
}
async function find(page: Page, id: string) {
  await page.getByLabel("Find a node").fill(id);
  await page.locator(".editor-search-results").getByRole("button", { name: new RegExp(`\\(${id}\\)$`) }).click();
  await expect(page.getByRole("complementary", { name: "Step inspector" })).toBeVisible();
}
async function persisted(request: APIRequestContext, id: string) { return (await (await request.get(`${api}/api/workflow-definitions/${id}`)).json()).versions[0].definition; }

test("contextual insertion preserves downstream connection and positions survive reload", async ({ page, request }) => {
  const item = await seed(request);
  await open(page, item.name);
  await page.getByRole("button", { name: "Add after Task 0 Next", exact: true }).click();
  await page.getByRole("complementary", { name: "Add step menu" }).getByRole("button", { name: /Implement/ }).click();
  await page.getByLabel("Display Name", { exact: true }).fill("Inserted task");
  await page.getByRole("button", { name: "Undo", exact: true }).click();
  await expect(page.getByLabel("Display Name", { exact: true })).toHaveValue("Implement");
  await page.getByRole("button", { name: "Redo", exact: true }).click();
  await expect(page.getByLabel("Display Name", { exact: true })).toHaveValue("Inserted task");
  await page.getByText("Advanced", { exact: true }).click();
  await page.getByLabel("Position X", { exact: true }).fill("987");
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  const saved = await persisted(request, item.id);
  expect(saved.steps.find((s: {id: string}) => s.id === "n0").nextStepId).toBe("step4");
  expect(saved.steps.find((s: {id: string}) => s.id === "step4").nextStepId).toBe("n1");
  expect(saved.editor.positions.step4.x).toBe(987);
  await page.reload(); await page.locator(".editor-workflow-name").click();
  await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name: item.name, exact: true }).click();
  await find(page, "step4"); await page.getByText("Advanced", { exact: true }).click();
  await expect(page.getByLabel("Position X", { exact: true })).toHaveValue("987");
});

test("task duplication deletion and keyboard undo preserve settings", async ({ page, request }) => {
  const item = await seed(request); await open(page, item.name); await find(page, "n0");
  await page.getByRole("button", { name: "Duplicate task", exact: true }).click();
  await expect(page.getByLabel("Display Name", { exact: true })).toHaveValue("Task 0 copy");
  await expect(page.getByLabel("Next step", { exact: true })).toHaveValue("");
  await page.getByRole("button", { name: "Delete", exact: true }).click();
  await expect(page.locator('.react-flow__node[data-id="n0-copy"]')).toHaveCount(0);
  await page.keyboard.press("Control+z");
  await expect(page.locator('.react-flow__node[data-id="n0-copy"]')).toHaveCount(1);
  await page.keyboard.press("Control+Shift+z");
  await expect(page.locator('.react-flow__node[data-id="n0-copy"]')).toHaveCount(0);
});

test("refresh keeps draft and navigation and version changes warn before discarding", async ({ page, request }) => {
  const item = await seed(request);
  await page.goto("/workflows"); await page.getByRole("button", { name: "Definitions", exact: true }).click();
  await page.locator(".editor-workflow-name").click(); await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name: item.name, exact: true }).click();
  await expect(page.locator(".editor-save-status")).toHaveText("Saved"); await find(page, "n0");
  await page.getByLabel("Display Name", { exact: true }).fill("Unsaved name");
  await page.locator(".editor-header").getByRole("button", { name: "Refresh", exact: true }).click();
  await expect(page.getByLabel("Display Name", { exact: true })).toHaveValue("Unsaved name");
  await page.getByRole("button", { name: "Workflows", exact: true }).click();
  await expect(page.getByRole("dialog")).toBeVisible(); await page.getByRole("button", { name: "Stay", exact: true }).click();
  await expect(page.getByLabel("Display Name", { exact: true })).toHaveValue("Unsaved name");
  await page.evaluate(() => history.back());
  await expect(page.getByRole("dialog")).toBeVisible(); await page.getByRole("button", { name: "Stay", exact: true }).click();
  let unloadPrompt = false; page.once("dialog", async dialog => { unloadPrompt = dialog.type() === "beforeunload"; await dialog.dismiss(); });
  const unloaded = page.waitForEvent("dialog");
  await page.evaluate(() => { setTimeout(() => location.reload(), 0); });
  await unloaded; await expect.poll(() => unloadPrompt).toBe(true);
  await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  await page.getByLabel("Display Name", { exact: true }).fill("Another edit");
  await page.getByLabel("Workflow version", { exact: true }).selectOption(item.version.id);
  await expect(page.getByRole("dialog")).toBeVisible(); await page.getByRole("button", { name: "Discard", exact: true }).click();
  await find(page, "n0"); await expect(page.getByLabel("Display Name", { exact: true })).toHaveValue("Task 0");
});

test("failed first version save retains the created definition for retry", async ({ page }) => {
  let creates = 0, failures = 0;
  page.on("request", request => { if (request.method() === "POST" && request.url().endsWith("/api/workflow-definitions")) creates++; });
  await page.route("**/api/workflow-definitions/*/versions", async route => { if (route.request().method() === "POST" && failures++ === 0) await route.fulfill({ status: 400, json: { errors: [{ code: "fixture", message: "Save rejected for test." }] } }); else await route.continue(); });
  await page.goto("/workflow-definitions"); await page.locator(".editor-workflow-name").click();
  await page.getByRole("button", { name: "New Definition", exact: true }).click();
  await page.getByLabel("Definition Name", { exact: true }).fill(`Retry ${Date.now()}`);
  await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.getByText("Save rejected for test.")).toBeVisible();
  await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  expect(creates).toBe(1);
});

test("validation locates incomplete loop and disabled version remains saveable", async ({ page, request }) => {
  const item = await seed(request); await open(page, item.name);
  await page.getByRole("button", { name: "+ Add Step", exact: true }).click();
  await page.getByRole("complementary", { name: "Add step menu" }).getByRole("button", { name: /Loop/ }).click();
  await expect(page.locator('.react-flow__node[data-id="step4"] .editor-node-error')).toBeVisible();
  await page.getByRole("button", { name: /^Problems/ }).click();
  await page.getByRole("region", { name: "Workflow problems" }).getByRole("button", { name: /needs an outgoing connection/ }).click();
  await expect(page.getByLabel("Repeat count")).toBeVisible();
  await page.getByRole("button", { name: "Workflow settings", exact: true }).click(); await page.getByLabel("Enabled", { exact: true }).uncheck();
  await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  expect((await persisted(request, item.id)).steps).toHaveLength(4);
});

test("view-only user can inspect and navigate but cannot edit", async ({ page, request }) => {
  const item = await seed(request); let mutations = 0;
  await page.route("**/api/auth/current-user", async route => { const response = await route.fetch(); await route.fulfill({ json: { ...await response.json(), canAdminister: false, canViewWorkflows: true } }); });
  page.on("request", request => { if (request.method() === "POST" && request.url().includes("workflow-definitions")) mutations++; });
  await open(page, item.name); await find(page, "n0");
  await expect(page.getByLabel("Display Name", { exact: true })).toBeDisabled(); await expect(page.getByRole("button", { name: "Save Version", exact: true })).toBeDisabled();
  await expect(page.getByRole("button", { name: "+ Add Step", exact: true })).toBeDisabled();
  await page.getByRole("button", { name: "Fit Selection", exact: true }).click(); await page.keyboard.press("Delete");
  await expect(page.locator('.react-flow__node[data-id="n0"]')).toHaveCount(1);
  expect(mutations).toBe(0);
});

test("large workflow arranges without overlaps and remains searchable at responsive sizes", async ({ page, request }) => {
  const item = await seed(request, 50); const errors: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  await open(page, item.name);
  const positions = await page.locator(".react-flow__node").evaluateAll(nodes => nodes.map(node => ({ id: node.getAttribute("data-id"), transform: (node as HTMLElement).style.transform, box: node.getBoundingClientRect().toJSON() })));
  expect(positions).toHaveLength(50); expect(new Set(positions.map(item => item.transform)).size).toBe(50);
  for (let i = 0; i < positions.length; i++) for (let j = i + 1; j < positions.length; j++) {
    const a = positions[i].box, b = positions[j].box;
    expect(a.right <= b.left || b.right <= a.left || a.bottom <= b.top || b.bottom <= a.top).toBeTruthy();
  }
  for (const width of [1600, 1280, 800]) {
    await page.setViewportSize({ width, height: 900 }); await find(page, "n25");
    await expect(page.getByLabel("Display Name", { exact: true })).toHaveValue("Task 25");
    await page.screenshot({ path: test.info().outputPath(`editor-${width}.png`), fullPage: true });
    const box = await page.getByRole("button", { name: "Save Version", exact: true }).boundingBox(); expect(box!.y + box!.height).toBeLessThan(900);
  }
  expect(errors).toEqual([]);
});


test("replacing connections is explicit and reversible", async ({ page, request }) => {
  const item = await seed(request); await open(page, item.name); await find(page, "n0");
  const next = page.getByLabel("Next step", { exact: true });
  await next.selectOption(JSON.stringify(["n2", "input"]));
  await page.getByRole("button", { name: "Stay", exact: true }).click();
  await expect(next).toHaveValue(JSON.stringify(["n1", "input"]));
  await next.selectOption(JSON.stringify(["n2", "input"]));
  await page.getByRole("button", { name: "Replace", exact: true }).click();
  await expect(next).toHaveValue(JSON.stringify(["n2", "input"]));
  await page.getByRole("button", { name: "Undo", exact: true }).click();
  await expect(next).toHaveValue(JSON.stringify(["n1", "input"]));
});

test("node keyboard movement and pointer drag are individually undoable", async ({ page, request }) => {
  const item = await seed(request); await open(page, item.name); await find(page, "n0");
  const node = page.locator('.react-flow__node[data-id="n0"]');
  const before = await node.getAttribute("style");
  await node.focus(); await page.keyboard.press("ArrowDown");
  await expect(node).not.toHaveAttribute("style", before!);
  await page.keyboard.press("Control+z"); await expect(node).toHaveAttribute("style", before!);
  const box = await node.boundingBox();
  await page.mouse.move(box!.x + 50, box!.y + 40); await page.mouse.down(); await page.mouse.move(box!.x + 100, box!.y + 80, { steps: 5 }); await page.mouse.up();
  await expect(node).not.toHaveAttribute("style", before!);
  await page.getByRole("button", { name: "Undo", exact: true }).click(); await expect(node).toHaveAttribute("style", before!);
});


test("multi-selection deletion is one undo operation", async ({ page, request }) => {
  const item = await seed(request); await open(page, item.name);
  await page.locator('.react-flow__node[data-id="n0"]').click();
  await page.getByRole("button", { name: "Close inspector", exact: true }).click();
  await page.getByRole("button", { name: "Fit All", exact: true }).click();
  await page.locator('.react-flow__node[data-id="n1"]').click({ modifiers: ["Control"] });
  await page.getByRole("button", { name: "Delete", exact: true }).click();
  await expect(page.locator('.react-flow__node')).toHaveCount(1);
  await page.getByRole("button", { name: "Undo", exact: true }).click();
  await expect(page.locator('.react-flow__node')).toHaveCount(3);
});

test("stale validation responses do not overwrite newer results", async ({ page, request }) => {
  const item = await seed(request); await open(page, item.name); await find(page, "n0");
  let release!: () => void;
  const gate = new Promise<void>(resolve => { release = resolve; });
  let oldRequested = false, newValidated = false;
  await page.route("**/api/workflow-definitions/validate", async route => {
    const name = route.request().postDataJSON().steps[0].displayName;
    if (name === "Old request") { oldRequested = true; await gate; await route.fulfill({ json: { isValid: false, errors: [{ code: "old", message: "Stale validation error", nodeId: "n0" }] } }); }
    else { await route.fulfill({ json: { isValid: true, errors: [] } }); if (name === "New request") newValidated = true; }
  });
  await page.getByLabel("Display Name", { exact: true }).fill("Old request");
  await expect.poll(() => oldRequested).toBe(true);
  await page.getByLabel("Display Name", { exact: true }).fill("New request");
  await expect.poll(() => newValidated).toBe(true);
  const staleResponse = page.waitForResponse(response => response.url().endsWith("/validate") && response.request().postDataJSON().steps[0].displayName === "Old request");
  release(); await staleResponse;
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await expect(page.getByText("Stale validation error")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Problems (0)", exact: true })).toBeVisible();
});
