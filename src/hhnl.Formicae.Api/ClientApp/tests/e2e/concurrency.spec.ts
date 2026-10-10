import { expect, test } from "@playwright/test";
const api = "http://127.0.0.1:5000";

test("global and definition instance limits persist independently of workflow versions", async ({ page, request }, testInfo) => {
  const errors: string[] = []; page.on("pageerror", error => errors.push(error.message));
  const name = `Concurrency ${Date.now()}`;
  const definition = await (await request.post(`${api}/api/workflow-definitions`, { data: { name } })).json();
  await page.goto("/settings");
  const global = page.getByRole("region", { name: "Global workflow instance limit" });
  await global.getByLabel("Global maximum active instances").fill("3");
  await global.getByRole("button", { name: "Save instance limit" }).click();
  await expect(global.getByRole("status")).toHaveText("Instance limit saved.");
  await page.reload(); await expect(global.getByLabel("Global maximum active instances")).toHaveValue("3");
  await page.goto("/workflow-definitions");
  await page.locator(".editor-workflow-name").click();
  await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name, exact: true }).click();
  await page.getByRole("button", { name: "Workflow settings", exact: true }).click();
  const perWorkflow = page.getByRole("region", { name: "Workflow instance limit" });
  await perWorkflow.getByLabel("Maximum active instances", { exact: true }).fill("2");
  await perWorkflow.getByRole("button", { name: "Save instance limit" }).click();
  await expect(perWorkflow.getByRole("status")).toHaveText("Instance limit saved.");
  const saved = await (await request.get(`${api}/api/workflow-definitions/${definition.id}`)).json();
  expect(saved.maxActiveInstances).toBe(2); expect(saved.versions).toHaveLength(0);
  await page.screenshot({ path: testInfo.outputPath("workflow-instance-limit.png"), fullPage: true });
  await page.goto("/settings");
  await global.getByLabel("Global maximum active instances").fill("");
  await global.getByRole("button", { name: "Save instance limit" }).click();
  await expect(global.getByRole("status")).toHaveText("Instance limit saved.");
  expect((await (await request.get(`${api}/api/workflow-concurrency`)).json()).maxActiveInstances).toBeNull();
  expect(errors).toEqual([]);
});

test("invalid instance limits are rejected without changing the saved limit", async ({ page, request }) => {
  await page.goto("/settings");
  const control = page.getByRole("region", { name: "Global workflow instance limit" });
  await expect(control.getByRole("button", { name: "Save instance limit" })).toBeEnabled();
  const initial = await (await request.get(`${api}/api/workflow-concurrency`)).json();
  await control.getByLabel("Global maximum active instances").fill("0");
  await control.getByRole("button", { name: "Save instance limit" }).click();
  await expect(control.getByRole("alert")).toContainText("positive whole number");
  expect((await (await request.get(`${api}/api/workflow-concurrency`)).json()).maxActiveInstances).toBe(initial.maxActiveInstances);
});
