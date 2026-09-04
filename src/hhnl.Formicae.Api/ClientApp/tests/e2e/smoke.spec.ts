import { expect, test } from "@playwright/test";

const apiUrl = "http://127.0.0.1:5000";

test("API health and version endpoints respond", async ({ request }) => {
  const healthResponse = await request.get(`${apiUrl}/healthz`);
  expect(healthResponse.ok()).toBe(true);
  expect(await healthResponse.text()).toBe("Healthy");

  const versionResponse = await request.get(`${apiUrl}/api/version`);
  expect(versionResponse.ok()).toBe(true);
  expect(await versionResponse.json()).toEqual({
    version: expect.stringMatching(/^\d+\.\d+\.\d+/)
  });
});

test("UI loads and navigates between primary pages", async ({ page }) => {
  await page.goto("/workflows");
  await expect(page.getByRole("heading", { level: 1, name: "Workflow Management" })).toBeVisible();

  await page.getByRole("button", { name: "Definitions", exact: true }).click();
  await expect(page).toHaveURL(/\/workflow-definitions$/);
  await expect(page.getByRole("heading", { level: 1, name: "Workflow Definitions" })).toBeVisible();
});

test("UI loads without page or console errors", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(`pageerror: ${error.message}`));
  page.on("console", message => {
    if (message.type() === "error") {
      errors.push(`console: ${message.text()}`);
    }
  });

  await page.goto("/workflows");
  await expect(page.getByRole("heading", { level: 1, name: "Workflow Management" })).toBeVisible();
  await page.waitForLoadState("networkidle");

  expect(errors).toEqual([]);
});
