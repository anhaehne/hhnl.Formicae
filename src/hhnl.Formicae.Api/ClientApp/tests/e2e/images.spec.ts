import { expect, test, type APIRequestContext } from "@playwright/test";
const api = "http://127.0.0.1:5000";
async function ready(request: APIRequestContext, image: { id: string; revision: number }) {
  const response = await request.post(`${api}/api/images/${image.id}/builds`, { data: { expectedRevision: image.revision } });
  expect(response.status(), await response.text()).toBe(202);
  const build = await response.json();
  await expect.poll(async () => (await (await request.get(`${api}/api/images/${image.id}/builds/${build.id}`)).json()).state, { timeout: 20_000 }).toBe("Ready");
  return (await request.get(`${api}/api/images/${image.id}/builds/${build.id}`)).json();
}

test("image catalog prepares builds and retains successful history after a failed rebuild", async ({ page, request }, testInfo) => {
  test.setTimeout(60_000);
  await page.goto("/images"); await page.getByRole("button", { name: "New image", exact: true }).click();
  const name = `Prepared tools ${Date.now()}`;
  await page.getByLabel("Image name", { exact: true }).fill(name);
  await page.getByLabel("Load Dockerfile", { exact: true }).setInputFiles({ name: "Dockerfile", mimeType: "text/plain", buffer: Buffer.from("FROM worker:0.22.0\nRUN echo tools") });
  await expect(page.getByLabel("Dockerfile", { exact: true })).toHaveValue("FROM worker:0.22.0\nRUN echo tools");
  await page.getByRole("button", { name: "Save image", exact: true }).click();
  await expect(page.getByText("Image source saved. Build it to prepare a selectable image.", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Build image", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Revision 1 · Ready", exact: true })).toBeVisible({ timeout: 20_000 });
  const image = (await (await request.get(`${api}/api/images`)).json()).find((item: { name: string }) => item.name === name);
  const first = (await (await request.get(`${api}/api/images/${image.id}/builds`)).json())[0];
  expect(first.reference).toMatch(/@sha256:[a-f0-9]{64}$/);
  await page.getByLabel("Dockerfile", { exact: true }).fill("FROM worker:0.22.0\n# FAIL_BUILD");
  await page.getByRole("button", { name: "Save image", exact: true }).click();
  await page.getByRole("button", { name: "Build image", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Revision 2 · Failed", exact: true })).toBeVisible({ timeout: 20_000 });
  await expect(page.getByRole("heading", { name: "Revision 1 · Ready", exact: true })).toBeVisible();
  await page.locator(".image-build").filter({ has: page.getByRole("heading", { name: "Revision 2 · Failed", exact: true }) }).locator("summary").click();
  await expect(page.locator(".image-build-logs").first()).not.toHaveText("Waiting for build logs.");
  await page.screenshot({ path: testInfo.outputPath("prepared-image-history.png"), fullPage: true });
  await page.getByRole("button", { name: "Archive image", exact: true }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Archive", exact: true }).click();
  await expect(page.getByText("Image archived. Pinned artifacts are retained.", { exact: true })).toBeVisible();
  expect((await (await request.get(`${api}/api/images/${image.id}/builds/${first.id}`)).json()).reference).toBe(first.reference);
});

test("Agent task pins an exact prepared build through rebuild reload and platform override", async ({ page, request }, testInfo) => {
  test.setTimeout(60_000);
  const response = await request.post(`${api}/api/images`, { data: { name: `Agent tools ${Date.now()}`, source: { dockerfile: "FROM worker:0.22.0\nRUN echo initial" } } });
  expect(response.ok(), await response.text()).toBeTruthy(); const image = await response.json(), build = await ready(request, image);
  const item = await (await request.post(`${api}/api/workflow-definitions`, { data: { name: `Prepared Agent ${Date.now()}` } })).json();
  const saved = await request.post(`${api}/api/workflow-definitions/${item.id}/versions`, { data: { isEnabled: true, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "agent", steps: [{ id: "agent", uses: "builtins.agent-task", customTask: { definition: { promptTemplate: "Inspect the prepared tools", inputs: [], outputs: [], runner: { kind: "agent", timeoutSeconds: 75 } } } }] } } });
  expect(saved.ok(), await saved.text()).toBeTruthy();
  async function open() {
    await page.goto("/workflow-definitions"); await page.locator(".editor-workflow-name").click();
    await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name: item.name, exact: true }).click();
    await page.getByLabel("Find a node").fill("agent"); await page.locator(".editor-search-results").getByRole("button", { name: /\(agent\)$/ }).click();
  }
  async function latest() { return (await (await request.get(`${api}/api/workflow-definitions/${item.id}`)).json()).versions[0].definition.steps.find((step: { id: string }) => step.id === "agent"); }
  await open(); await page.getByLabel("Execution image", { exact: true }).selectOption("managed");
  await page.getByLabel("Prepared image", { exact: true }).selectOption(image.id);
  await page.getByLabel("Prepared build", { exact: true }).selectOption(build.id);
  await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.locator(".editor-save-status")).toHaveText("Saved");
  expect((await latest()).imageSnapshot.reference).toBe(build.reference); expect((await latest()).customTask.snapshot.runner.timeoutSeconds).toBe(75);
  await request.put(`${api}/api/images/${image.id}`, { data: { name: image.name, expectedRevision: 1, source: { dockerfile: "FROM worker:0.22.0\nRUN echo revised" } } });
  const second = await ready(request, { ...image, revision: 2 }); expect(second.reference).not.toBe(build.reference);
  await open(); await expect(page.getByLabel("Prepared build", { exact: true })).toHaveValue(build.id);
  await expect(page.locator(".image-reference")).toContainText(build.reference); expect((await latest()).imageSnapshot.reference).toBe(build.reference);
  await page.screenshot({ path: testInfo.outputPath("agent-prepared-image.png"), fullPage: true });
  await page.getByLabel("Execution image", { exact: true }).selectOption("platform");
  await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.locator(".editor-save-status")).toHaveText("Saved");
  expect((await latest()).imageSelection.mode).toBe("platform"); expect((await latest()).imageSnapshot).toBeFalsy();
  expect((await latest()).customTask.snapshot.runner.timeoutSeconds).toBe(75);
});
