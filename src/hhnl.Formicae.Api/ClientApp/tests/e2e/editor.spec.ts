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

test("refresh keeps draft and navigation and version changes warn before discarding", async ({ page, request }, testInfo) => {
  const item = await seed(request);
  await page.goto("/workflows"); await page.getByRole("button", { name: "Definitions", exact: true }).click();
  await page.locator(".editor-workflow-name").click(); await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name: item.name, exact: true }).click();
  await expect(page.locator(".editor-save-status")).toHaveText("Saved"); await find(page, "n0");
  await page.getByLabel("Display Name", { exact: true }).fill("Unsaved name");
  await page.locator(".editor-header").getByRole("button", { name: "Refresh", exact: true }).click();
  await expect(page.getByLabel("Display Name", { exact: true })).toHaveValue("Unsaved name");
  await page.getByRole("button", { name: "Workflows", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Discard your changes?" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Stay", exact: true })).toBeFocused();
  await page.screenshot({ path: testInfo.outputPath("discard-dialog.png") });
  await page.getByRole("button", { name: "Stay", exact: true }).click();
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


test("replacing connections is explicit and reversible", async ({ page, request }, testInfo) => {
  const item = await seed(request); await open(page, item.name); await find(page, "n0");
  const next = page.getByLabel("Next step", { exact: true });
  await next.selectOption(JSON.stringify(["n2", "input"]));
  const dialog = page.getByRole("dialog", { name: "Replace connection?" });
  await expect(dialog.getByRole("button", { name: "Stay", exact: true })).toHaveCount(0);
  await expect(dialog.getByRole("button", { name: "Cancel", exact: true })).toBeFocused();
  await expect.poll(async () => {
    const cancel = await dialog.getByRole("button", { name: "Cancel", exact: true }).boundingBox();
    const replace = await dialog.getByRole("button", { name: "Replace", exact: true }).boundingBox();
    return !!cancel && !!replace && Math.abs(cancel.y - replace.y) < 2 && cancel.x + cancel.width <= replace.x;
  }).toBe(true);
  await page.screenshot({ path: testInfo.outputPath("replace-dialog.png") });
  await page.keyboard.press("Escape");
  await expect(dialog).not.toBeVisible();
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

for (const title of ["Plan", "Implement", "Create pull request", "Address comments", "Trigger", "Loop", "Parallel", "Decision"]) {
  test(`adding ${title} keeps the canvas usable through validation`, async ({ page, request }, testInfo) => {
    const item = await seed(request, 4);
    await page.setViewportSize({ width: 1280, height: 720 });
    await page.addInitScript(() => {
      const NativeObserver = window.ResizeObserver;
      window.ResizeObserver = class extends NativeObserver {
        constructor(callback: ResizeObserverCallback) { super((entries, observer) => { window.setTimeout(() => callback(entries, observer), 250); }); }
      };
    });
    await open(page, item.name);
    const crashes: string[] = [];
    page.on("pageerror", error => crashes.push(error.message));
    await page.getByRole("button", { name: "+ Add Step", exact: true }).click();
    await page.getByLabel("Search step types").fill(title);
    const validation = page.waitForResponse(response => response.url().endsWith("/workflow-definitions/validate") && response.request().postData()?.includes('"step5"') === true);
    await page.getByRole("complementary", { name: "Add step menu" }).getByRole("button", { name: new RegExp(`^${title} `) }).click();
    await validation;
    await expect(page.getByLabel("Display Name", { exact: true })).toHaveValue(title);
    await expect(page.locator('.react-flow__node[data-id="step5"]')).toBeInViewport();
    await expect(page.locator(".react-flow__viewport")).not.toHaveAttribute("style", /NaN|Infinity/);
    await page.getByRole("button", { name: /Problems \(/ }).click();
    await page.screenshot({ path: testInfo.outputPath("added-node.png") });
    await expect.poll(async () => (await page.locator(".editor-canvas").boundingBox())?.height ?? 0).toBeGreaterThan(200);
    await expect(page.locator(".react-flow__node")).toHaveCount(5);
    await page.getByRole("button", { name: "Fit All", exact: true }).click();
    for (const id of ["n0", "n1", "n2", "n3", "step5"]) await expect(page.locator(`.react-flow__node[data-id="${id}"]`)).toBeInViewport();
    await page.getByLabel("Display Name", { exact: true }).fill(`${title} edited`);
    await page.getByRole("button", { name: "Undo", exact: true }).click();
    await expect(page.getByLabel("Display Name", { exact: true })).toHaveValue(title);
    await find(page, "step5");
    const added = page.locator('.react-flow__node[data-id="step5"]');
    await expect(added).toHaveCSS("visibility", "visible");
    await page.evaluate(() => {
      const hidden: string[] = [];
      const observer = new MutationObserver(records => {
        for (const record of records) {
          const node = record.target as HTMLElement;
          if (node.style.visibility === "hidden" || /visibility: hidden/.test(record.oldValue ?? "")) hidden.push(node.dataset.id ?? "unknown");
        }
      });
      document.querySelectorAll(".react-flow__node").forEach(node => observer.observe(node, { attributes: true, attributeFilter: ["style"], attributeOldValue: true }));
      Object.assign(window, { dragVisibility: { hidden, observer } });
    });
    const box = await added.boundingBox();
    await page.mouse.move(box!.x + 50, box!.y + 40); await page.mouse.down();
    await page.mouse.move(box!.x + 100, box!.y + 80, { steps: 12 }); await page.mouse.up();
    const hidden = await page.evaluate(() => {
      const state = (window as unknown as { dragVisibility: { hidden: string[]; observer: MutationObserver } }).dragVisibility;
      state.observer.disconnect(); return state.hidden;
    });
    expect(hidden, "Measured nodes must not be hidden again while dragging").toEqual([]);
    expect(crashes).toEqual([]);
  });
}


test("parallel branches connect through the inspector and persist named joins", async ({ page, request }, testInfo) => {
  const item = await seed(request); await open(page, item.name);
  await page.getByRole("button", { name: "+ Add Step", exact: true }).click();
  await page.getByRole("complementary", { name: "Add step menu" }).getByRole("button", { name: /Parallel/ }).click();
  await expect(page.getByRole("button", { name: "Duplicate task", exact: true })).toBeDisabled();
  await expect(page.getByLabel("Branch count", { exact: true })).toHaveValue("2");
  await page.getByRole("button", { name: "Set as Start Step", exact: true }).click();
  await page.getByLabel("Branch 1", { exact: true }).selectOption(JSON.stringify(["n0", "input"]));
  await page.getByLabel("Branch 2", { exact: true }).selectOption(JSON.stringify(["n1", "input"]));
  await page.getByLabel("Next step", { exact: true }).selectOption(JSON.stringify(["n2", "input"]));
  for (const id of ["n0", "n1"]) {
    await find(page, id);
    await page.getByLabel("Next step", { exact: true }).selectOption(JSON.stringify(["step4", "join"]));
    await page.getByRole("dialog").getByRole("button", { name: "Replace", exact: true }).click();
  }
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  const saved = await persisted(request, item.id);
  expect(saved.startStepId).toBe("step4");
  expect(saved.steps.find((step: { id: string }) => step.id === "step4").parallel.branchStepIds).toEqual(["n0", "n1"]);
  expect(saved.steps.find((step: { id: string }) => step.id === "step4").nextStepId).toBe("n2");
  expect(saved.steps.filter((step: { nextStepPort?: string }) => step.nextStepPort === "join")).toHaveLength(2);
  await open(page, item.name); await find(page, "step4");
  await expect(page.getByLabel("Branch 1", { exact: true })).toHaveValue(JSON.stringify(["n0", "input"]));
  await expect(page.locator('.react-flow__node[data-id="step4"] [data-handleid="join"]')).toBeVisible();
  await page.getByRole("button", { name: "Arrange", exact: true }).click();
  await expect(page.getByRole("button", { name: "Arrange", exact: true })).toBeEnabled();
  await expect(page.locator(".react-flow__viewport")).not.toHaveAttribute("style", /NaN|Infinity/);
  await page.getByRole("button", { name: "Fit All", exact: true }).click();
  const groupBox = await page.locator('.react-flow__node[data-id="step4"]').boundingBox();
  for (const id of ["n0", "n1"]) expect((await page.locator(`.react-flow__node[data-id="${id}"]`).boundingBox())!.x).toBeGreaterThan(groupBox!.x + groupBox!.width);
  for (const id of ["n0", "n1", "n2", "step4"]) await expect(page.locator(`.react-flow__node[data-id="${id}"]`)).toBeInViewport();
  for (const width of [1600, 800]) {
    await page.setViewportSize({ width, height: 900 });
    await page.getByRole("button", { name: "Fit All", exact: true }).click();
    await page.screenshot({ path: testInfo.outputPath(`parallel-editor-${width}.png`) });
    await expect(page.getByLabel("Branch 1", { exact: true })).toBeVisible();
  }
});

test("parallel branch resizing and contextual Plan insertion remain undoable", async ({ page, request }) => {
  const item = await seed(request); await open(page, item.name);
  await page.getByRole("button", { name: "+ Add Step", exact: true }).click();
  await page.getByRole("complementary", { name: "Add step menu" }).getByRole("button", { name: /Parallel/ }).click();
  await page.getByLabel("Branch count", { exact: true }).selectOption("3");
  await page.getByLabel("Branch 3", { exact: true }).selectOption(JSON.stringify(["n0", "input"]));
  await page.getByLabel("Branch count", { exact: true }).selectOption("2");
  await expect(page.getByLabel("Branch 3", { exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Undo", exact: true }).click();
  await expect(page.getByLabel("Branch count", { exact: true })).toHaveValue("3");
  await expect(page.getByLabel("Branch 3", { exact: true })).toHaveValue(JSON.stringify(["n0", "input"]));
  await page.getByLabel("Branch count", { exact: true }).selectOption("8");
  await expect(page.locator('.react-flow__node[data-id="step4"] [data-handleid="branch:7"]')).toBeAttached();
  await page.getByLabel("Branch count", { exact: true }).selectOption("3");
  await find(page, "step4");
  await page.getByRole("button", { name: "Add after Parallel Branch 3", exact: true }).click();
  const menu = page.getByRole("complementary", { name: "Add step menu" });
  await expect(menu.getByRole("button", { name: /Implement/ })).toBeDisabled();
  await expect(menu.getByRole("button", { name: /Loop/ })).toBeDisabled();
  await expect(menu.getByRole("button", { name: /Parallel/ })).toBeDisabled();
  await menu.getByRole("button", { name: /^Plan / }).click();
  await expect(page.getByLabel("Next step", { exact: true })).toHaveValue(JSON.stringify(["n0", "input"]));
  await page.getByRole("button", { name: "Workflow settings", exact: true }).click();
  await page.getByLabel("Enabled", { exact: true }).uncheck();
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  const saved = await persisted(request, item.id);
  expect(saved.steps.find((step: { id: string }) => step.id === "step4").parallel.branchStepIds).toEqual(["", "", "step5"]);
  expect(saved.steps.find((step: { id: string }) => step.id === "step5").nextStepId).toBe("n0");
});


for (const outcome of [true, false]) test(`decision ${outcome} routes persist with contextual insertion`, async ({ page, request }, testInfo) => {
  const item = await seed(request); await open(page, item.name);
  await page.getByRole("button", { name: "+ Add Step", exact: true }).click();
  await page.getByRole("complementary", { name: "Add step menu" }).getByRole("button", { name: /^Decision / }).click();
  await expect(page.getByRole("button", { name: "Duplicate task", exact: true })).toBeDisabled();
  await expect(page.getByLabel("Next step", { exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: /Problems \([1-9]/ }).click();
  const problems = page.getByRole("region", { name: "Workflow problems" });
  await problems.getByRole("button").nth(1).click();
  await expect(page.getByLabel("Display Name", { exact: true })).toHaveValue("Decision");
  await problems.getByRole("button", { name: "Close", exact: true }).click();
  await page.getByLabel("Value type", { exact: true }).selectOption("boolean");
  await page.getByLabel("Source value", { exact: true }).selectOption(String(outcome));
  await page.getByLabel("Compare to", { exact: true }).selectOption("true");
  await expect(page.getByLabel("Operator", { exact: true }).getByRole("option", { name: "Contains", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Set as Start Step", exact: true }).click();
  await page.getByLabel("True route", { exact: true }).selectOption(JSON.stringify(["n0", "input"]));
  await page.getByLabel("False route", { exact: true }).selectOption(JSON.stringify(["n1", "input"]));
  for (const [route, target] of [["True", "n0"], ["False", "n1"]]) {
    await find(page, "step4");
    await page.getByRole("button", { name: `Add after Decision ${route}`, exact: true }).click();
    await page.getByRole("complementary", { name: "Add step menu" }).getByRole("button", { name: /^Plan / }).click();
    await expect(page.getByLabel("Next step", { exact: true })).toHaveValue(JSON.stringify([target, "input"]));
  }
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  const saved = await persisted(request, item.id);
  const decision = saved.steps.find((step: { id: string }) => step.id === "step4");
  expect(decision.nextStepId ?? null).toBeNull();
  expect(decision.decision.trueStepId).toBe("step5"); expect(decision.decision.falseStepId).toBe("step6");
  expect(decision.decision.condition).toMatchObject({ source: "literal", valueType: "boolean", value: outcome, compareTo: true });
  await open(page, item.name); await find(page, "step4");
  await expect(page.getByLabel("Source value", { exact: true })).toHaveValue(String(outcome));
  await expect(page.getByLabel("True route", { exact: true })).toHaveValue(JSON.stringify(["step5", "input"]));
  await expect(page.getByLabel("False route", { exact: true })).toHaveValue(JSON.stringify(["step6", "input"]));
  await page.getByRole("button", { name: "Arrange", exact: true }).click();
  await expect(page.getByRole("button", { name: "Arrange", exact: true })).toBeEnabled();
  for (const width of [1600, 800]) {
    await page.setViewportSize({ width, height: 900 }); await page.getByRole("button", { name: "Fit All", exact: true }).click();
    await expect(page.locator(".react-flow__viewport")).not.toHaveAttribute("style", /NaN|Infinity/);
    await page.screenshot({ path: testInfo.outputPath(`decision-${width}.png`) });
  }
});

test("decision task output choices exclude loop and parallel bodies and remain read-only", async ({ page, request }) => {
  const item = await seed(request);
  const response = await request.post(`${api}/api/workflow-definitions/${item.id}/versions`, { data: { isEnabled: false, isDefault: false, definition: {
    schema: "formicae.workflow/v1alpha3", startStepId: "ordinary", steps: [
      { id: "ordinary", uses: "builtins.plan", nextStepId: "decision" },
      { id: "decision", uses: "builtins.decision", decision: { condition: { source: "taskOutput", valueType: "string", reference: "ordinary", operator: "exists", missingValue: "error" }, trueStepId: "parallel", falseStepId: "loop" } },
      { id: "parallel", uses: "builtins.parallel", parallel: { branchStepIds: ["branch1", "branch2"] } },
      { id: "branch1", uses: "builtins.plan", nextStepId: "parallel", nextStepPort: "join" },
      { id: "branch2", uses: "builtins.plan", nextStepId: "parallel", nextStepPort: "join" },
      { id: "loop", uses: "builtins.loop", loop: { bodyStepId: "body", repeatCount: 2, maxIterations: 2 } },
      { id: "body", uses: "builtins.plan", nextStepId: "loop", nextStepPort: "return" }
    ] } } }); expect(response.ok()).toBeTruthy();
  await open(page, item.name); await find(page, "decision");
  const options = page.getByLabel("Source task", { exact: true }).getByRole("option");
  await expect(options).toHaveText(["Choose a task", "ordinary (ordinary)"]);
  await page.getByLabel("Value type", { exact: true }).selectOption("number");
  await page.getByLabel("Operator", { exact: true }).selectOption("greaterThan");
  await page.getByLabel("Compare to", { exact: true }).fill("12.5");
  await page.getByRole("button", { name: "Undo", exact: true }).click();
  await expect(page.getByLabel("Compare to", { exact: true })).toHaveValue("0");
  await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  await page.route("**/api/auth/current-user", async route => { const response = await route.fetch(); await route.fulfill({ json: { ...await response.json(), canAdminister: false, canViewWorkflows: true } }); });
  await open(page, item.name); await find(page, "decision");
  await expect(page.getByLabel("Condition source", { exact: true })).toBeDisabled();
  await expect(page.getByLabel("True route", { exact: true })).toBeDisabled();
  await expect(page.getByRole("button", { name: "Save Version", exact: true })).toBeDisabled();
});

test("workflow decision history shows both outcomes and recorded input", async ({ page }, testInfo) => {
  const id = "11111111-1111-1111-1111-111111111111";
  const workflow = { workflowId: id, issueUrl: "https://example.com/issues/1", repositoryUrl: "https://example.com/repo", status: "Completed", currentStep: "Done", createdAt: "2026-09-06T10:00:00Z", updatedAt: "2026-09-06T10:01:00Z" };
  await page.route("**/api/workflows**", route => {
    const path = new URL(route.request().url()).pathname;
    const json = path.endsWith("/decisions") ? [true, false].map((booleanResult, index) => ({ id: `outcome-${index}`, workflowId: id, nodeId: `decision-${index}`, booleanResult, configuredTargetId: `route-${index}`, selectedTargetId: `route-${index}`, evaluatedAt: "2026-09-06T10:00:30Z", inputJson: JSON.stringify({ source: "workflowField", reference: "baseBranch", valueType: "string", value: "main" }), sourceTaskRunId: null }))
      : path === "/api/workflows" ? [workflow] : path === `/api/workflows/${id}` ? workflow : [];
    return route.fulfill({ json });
  });
  await page.goto("/workflows");
  const history = page.getByRole("region", { name: "Decision history" });
  await expect(history.getByText("Decision decision-0", { exact: true })).toBeVisible();
  await expect(history.getByText("True", { exact: true })).toBeVisible();
  await expect(history.getByText("False", { exact: true })).toBeVisible();
  await expect(history.getByText("route-1", { exact: true })).toBeVisible();
  await history.getByRole("button", { name: "Expand Evaluated input", exact: true }).first().click();
  await expect(history.locator("pre").first()).toContainText('"value": "main"');
  await page.screenshot({ path: testInfo.outputPath("decision-history.png"), fullPage: true });
});
