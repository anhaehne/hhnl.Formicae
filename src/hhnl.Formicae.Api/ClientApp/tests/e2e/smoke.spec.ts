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

test("workflow editor round-trips loop settings", async ({ page, request }) => {
  const name = `Smoke loop ${Date.now()}`;
  const definitionResponse = await request.post(`${apiUrl}/api/workflow-definitions`, { data: { name } });
  expect(definitionResponse.ok()).toBe(true);
  const definition = await definitionResponse.json();
  const versionResponse = await request.post(`${apiUrl}/api/workflow-definitions/${definition.id}/versions`, {
    data: {
      isEnabled: true,
      isDefault: false,
      definition: {
        schema: "formicae.workflow/v1alpha2",
        startStepId: "plan",
        steps: [
          { id: "plan", uses: "builtins.plan", nextStepId: "plan", displayName: "Plan repeatedly" },
          { id: "exit", uses: "builtins.implement", nextStepId: null, displayName: "Exit" }
        ],
        loops: [{ id: "planning", bodyStepIds: ["plan"], repeatCount: 2, maxIterations: 3, timeoutSeconds: 60, exitStepId: "exit" }]
      }
    }
  });
  expect(versionResponse.ok()).toBe(true);

  await page.goto("/workflow-definitions");
  await page.getByRole("button", { name: new RegExp(name) }).click();
  await expect(page.getByLabel("Body steps (ordered)")).toHaveValue("plan");
  await expect(page.getByLabel("Repeat count")).toHaveValue("2");
  await expect(page.getByLabel("Maximum iterations")).toHaveValue("3");
  await expect(page.getByLabel("Timeout seconds (optional)")).toHaveValue("60");
  await expect(page.getByLabel("Exit step")).toHaveValue("exit");

  await page.getByLabel("Repeat count").fill("3");
  await page.getByRole("button", { name: "Save Version" }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  await page.reload();
  await page.getByRole("button", { name: new RegExp(name) }).click();
  await expect(page.getByLabel("Repeat count")).toHaveValue("3");
});
