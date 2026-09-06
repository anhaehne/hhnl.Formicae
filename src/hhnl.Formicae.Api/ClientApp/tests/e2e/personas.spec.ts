import { expect, test, type Page, type APIRequestContext } from "@playwright/test";
const api = "http://127.0.0.1:5000";
async function persona(request: APIRequestContext, name: string) {
  const response = await request.post(`${api}/api/personas`, { data: { name: `${name} ${Date.now()}`, instructions: "Original instructions", tone: "Concise", operatingConstraints: "Read evidence first" } });
  expect(response.ok()).toBeTruthy(); return response.json();
}
async function definition(request: APIRequestContext, defaultPersonaId?: string) {
  const result = await (await request.post(`${api}/api/workflow-definitions`, { data: { name: `Persona workflow ${Date.now()}` } })).json();
  const response = await request.post(`${api}/api/workflow-definitions/${result.id}/versions`, { data: { isEnabled: true, isDefault: false, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "n0", defaultPersonaId, steps: [0,1,2].map(i => ({ id: `n${i}`, displayName: `Task ${i}`, uses: "builtins.plan", nextStepId: i < 2 ? `n${i+1}` : null })) } } });
  expect(response.ok()).toBeTruthy(); return result;
}
async function open(page: Page, name: string) { await page.goto("/workflow-definitions"); await page.locator(".editor-workflow-name").click(); await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name, exact: true }).click(); await expect(page.locator(".editor-save-status")).toHaveText("Saved"); }
async function find(page: Page, id: string) { await page.getByLabel("Find a node").fill(id); await page.locator(".editor-search-results").getByRole("button", { name: new RegExp(`\\(${id}\\)$`) }).click(); }
async function latest(request: APIRequestContext, id: string) { return (await (await request.get(`${api}/api/workflow-definitions/${id}`)).json()).versions[0].definition; }

test("persona catalog preserves conflicting edits and requires explicit reload and deletion", async ({ page, request }, testInfo) => {
  await page.goto("/personas");
  await page.getByRole("complementary", { name: "Persona catalog" }).getByRole("button", { name: /Built-in/ }).click();
  await expect(page.getByLabel("Instructions", { exact: true })).toBeDisabled();
  await expect(page.getByRole("button", { name: "Delete persona", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "New persona", exact: true }).click();
  const name = `Catalog ${Date.now()}`;
  await page.getByLabel("Persona name", { exact: true }).fill(name);
  await page.getByLabel("Instructions", { exact: true }).fill("Original context");
  await page.getByRole("button", { name: "Save persona", exact: true }).click();
  await expect(page.getByText("Persona saved.", { exact: true })).toBeVisible();
  const saved = (await (await request.get(`${api}/api/personas`)).json()).find((item: { name: string }) => item.name === name);
  const changed = await request.put(`${api}/api/personas/${saved.id}`, { data: { ...saved, instructions: "Another operator's context", expectedRevision: saved.revision } }); expect(changed.ok()).toBeTruthy();
  await page.getByLabel("Instructions", { exact: true }).fill("My unsaved context");
  await page.getByRole("button", { name: "Save persona", exact: true }).click();
  await expect(page.getByText(/Your edits are retained/)).toBeVisible();
  await page.getByRole("button", { name: "Refresh personas", exact: true }).click();
  await expect(page.getByLabel("Instructions", { exact: true })).toHaveValue("My unsaved context");
  await page.route("**/api/personas", route => route.fulfill({ status: 503, json: { error: "Catalog temporarily unavailable" } }));
  await page.getByRole("button", { name: "Reload current revision", exact: true }).click();
  await page.screenshot({ path: testInfo.outputPath("persona-discard-dialog.png") });
  await page.getByRole("dialog").getByRole("button", { name: "Discard", exact: true }).click();
  await expect(page.getByRole("alert")).toHaveText("Catalog temporarily unavailable");
  await expect(page.getByLabel("Instructions", { exact: true })).toHaveValue("My unsaved context");
  await expect(page.getByLabel("Persona name", { exact: true })).toHaveValue(name);
  await page.unroute("**/api/personas");
  let releaseReload!: () => void; const reloadGate = new Promise<void>(resolve => { releaseReload = resolve; });
  await page.route("**/api/personas", async route => { await reloadGate; await route.continue(); });
  await page.getByRole("button", { name: "Reload current revision", exact: true }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Discard", exact: true }).click();
  await expect(page.getByLabel("Instructions", { exact: true })).toBeDisabled();
  await expect(page.getByRole("button", { name: "New persona", exact: true })).toBeDisabled();
  releaseReload();
  await expect(page.getByLabel("Instructions", { exact: true })).toHaveValue("Another operator's context");
  await page.unroute("**/api/personas");
  await page.getByLabel("Tone", { exact: true }).fill("Direct");
  await page.getByRole("button", { name: "Save persona", exact: true }).click(); await expect(page.getByText("Persona saved.", { exact: true })).toBeVisible();
  for (const width of [1600, 800]) { await page.setViewportSize({ width, height: 900 }); if (width === 800) await expect.poll(async () => page.locator(".side-nav").evaluate(element => element.getBoundingClientRect().right)).toBeLessThanOrEqual(1); await page.screenshot({ path: testInfo.outputPath(`personas-${width}.png`), fullPage: true }); }
  await page.getByRole("button", { name: "Delete persona", exact: true }).click();
  await expect(page.getByRole("dialog")).toContainText("Existing workflow versions retain");
  await page.screenshot({ path: testInfo.outputPath("persona-delete-dialog.png") });
  await page.getByRole("dialog").getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(page.getByLabel("Persona name", { exact: true })).toHaveValue(name);
  await page.getByRole("button", { name: "Delete persona", exact: true }).click(); await page.getByRole("dialog").getByRole("button", { name: "Delete", exact: true }).click();
  await expect(page.getByText(/Persona deleted/)).toBeVisible();
});

test("workflow personas preserve inheritance overrides and saved revision previews", async ({ page, request }, testInfo) => {
  const first = await persona(request, "Reviewer"), second = await persona(request, "Writer"), item = await definition(request);
  await open(page, item.name); await page.getByRole("button", { name: "Workflow settings", exact: true }).click();
  await page.getByLabel("Workflow persona", { exact: true }).selectOption(first.id);
  await find(page, "n1"); await page.getByLabel("Step persona", { exact: true }).selectOption(second.id);
  await find(page, "n2"); await page.getByLabel("Step persona", { exact: true }).selectOption("default");
  await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.locator(".editor-save-status")).toHaveText("Saved");
  const doc = await latest(request, item.id); expect(doc.defaultPersonaId).toBe(first.id); expect(doc.steps.map((step: { personaSnapshot: { id: string } }) => step.personaSnapshot.id)).toEqual([first.id, second.id, "default"]);
  await request.put(`${api}/api/personas/${first.id}`, { data: { ...first, instructions: "Updated instructions", expectedRevision: 1 } });
  await page.getByRole("button", { name: "Refresh", exact: true }).click();
  await page.getByRole("button", { name: "Workflow settings", exact: true }).click();
  await expect(page.getByText("Saved version uses revision 1; Save Version will use revision 2.", { exact: true })).toBeVisible();
  await find(page, "n0");
  await expect(page.getByText("Saved version uses revision 1; Save Version will use revision 2.", { exact: true })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("persona-revision-preview.png") });
  await page.getByLabel("Display Name", { exact: true }).fill("Saved at revision 2");
  await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.locator(".editor-save-status")).toHaveText("Saved");
  await expect(page.getByText(`Saved persona: ${first.name} · revision 2`, { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Undo", exact: true }).click();
  await expect(page.getByLabel("Display Name", { exact: true })).toHaveValue("Task 0");
  await expect(page.getByText(`Saved persona: ${first.name} · revision 2`, { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Redo", exact: true }).click();
  await page.getByRole("button", { name: "Duplicate task", exact: true }).click(); await expect(page.getByLabel("Step persona", { exact: true })).toHaveValue("");
  await page.getByRole("button", { name: "Undo", exact: true }).click(); await find(page, "n1");
  await page.getByRole("combobox", { name: "Task", exact: true }).selectOption("builtins.create-pull-request"); await expect(page.getByLabel("Step persona", { exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Undo", exact: true }).click(); await expect(page.getByLabel("Step persona", { exact: true })).toHaveValue(second.id);
});

test("deleted persona selections explain saved execution and support disabled drafts", async ({ page, request }) => {
  const selected = await persona(request, "Retired"), item = await definition(request, selected.id);
  expect((await request.delete(`${api}/api/personas/${selected.id}?expectedRevision=1`)).ok()).toBeTruthy();
  await open(page, item.name); await find(page, "n0");
  await expect(page.getByText(/The saved version remains runnable/)).toBeVisible();
  await page.getByRole("button", { name: "Workflow settings", exact: true }).click();
  await expect(page.getByLabel("Workflow persona", { exact: true })).toHaveValue(selected.id);
  await page.getByLabel("Enabled", { exact: true }).uncheck();
  await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.locator(".editor-save-status")).toHaveText("Saved");
  await open(page, item.name); await page.getByRole("button", { name: "Workflow settings", exact: true }).click();
  await expect(page.getByLabel("Workflow persona", { exact: true })).toHaveValue(selected.id);
  await page.getByLabel("Workflow persona", { exact: true }).selectOption(""); await page.getByLabel("Enabled", { exact: true }).check();
  await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.locator(".editor-save-status")).toHaveText("Saved");
});

test("persona catalog is inspectable but immutable for workflow viewers", async ({ page, request }) => {
  const selected = await persona(request, "Viewer persona");
  await page.route("**/api/auth/current-user", async route => { const response = await route.fetch(); await route.fulfill({ json: { ...await response.json(), canAdminister: false, canViewWorkflows: true } }); });
  await page.goto("/personas"); await page.getByRole("complementary", { name: "Persona catalog" }).getByRole("button", { name: new RegExp(selected.name) }).click();
  await expect(page.getByLabel("Instructions", { exact: true })).toBeDisabled(); await expect(page.getByRole("button", { name: "Save persona", exact: true })).toBeDisabled(); await expect(page.getByRole("button", { name: "Delete persona", exact: true })).toBeDisabled();
});

test("a delayed version save preserves later persona and graph edits as unsaved", async ({ page, request }) => {
  const first = await persona(request, "Initial"), second = await persona(request, "Later"), item = await definition(request);
  await open(page, item.name); await find(page, "n0"); await page.getByLabel("Step persona", { exact: true }).selectOption(first.id);
  let release!: () => void; const gate = new Promise<void>(resolve => { release = resolve; }); let requested = false;
  await page.route(`**/api/workflow-definitions/${item.id}/versions`, async route => { if (route.request().method() !== "POST") return route.continue(); requested = true; await gate; await route.fulfill({ response: await route.fetch() }); });
  await page.locator(".editor-workflow-name").click();
  await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect.poll(() => requested).toBe(true);
  await expect(page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name: item.name, exact: true })).toBeDisabled();
  await page.getByRole("button", { name: "Close workflow switcher", exact: true }).click();
  await page.getByLabel("Display Name", { exact: true }).fill("Edited during save"); await page.getByLabel("Step persona", { exact: true }).selectOption(second.id);
  await expect(page.getByRole("button", { name: "Save Version", exact: true })).toBeDisabled();
  await page.getByRole("button", { name: "Workflows", exact: true }).click(); await expect(page.getByRole("dialog")).toBeVisible(); await page.getByRole("button", { name: "Stay", exact: true }).click();
  release(); await expect(page.getByRole("button", { name: "Save Version", exact: true })).toBeEnabled();
  await expect(page.locator(".editor-save-status")).toHaveText("Unsaved changes"); await expect(page.getByLabel("Display Name", { exact: true })).toHaveValue("Edited during save"); await expect(page.getByLabel("Step persona", { exact: true })).toHaveValue(second.id);
  const saved = await latest(request, item.id); expect(saved.steps[0].displayName).toBe("Task 0"); expect(saved.steps[0].personaSnapshot.id).toBe(first.id);
  await page.getByRole("button", { name: "Workflows", exact: true }).click(); await expect(page.getByRole("dialog")).toBeVisible(); await page.getByRole("button", { name: "Stay", exact: true }).click();
  await page.unroute(`**/api/workflow-definitions/${item.id}/versions`); await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.locator(".editor-save-status")).toHaveText("Saved");
});
