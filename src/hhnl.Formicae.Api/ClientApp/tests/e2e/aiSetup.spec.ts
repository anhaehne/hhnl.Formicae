import { expect, test, type Page } from "@playwright/test";

const codex = { id: "setup-codex", name: "Setup Codex", agentKind: "Acp", acpProvider: "Codex", authMethod: "CodexSubscription", model: "saved-model" };
const other = { id: "setup-other", name: "Setup Other", agentKind: "OpenHands", authMethod: "ApiKey", model: "other-model" };

async function mockSettings(page: Page) {
  let settings = [codex, other];
  await page.route("**/api/ai-settings", async route => {
    if (route.request().method() === "PUT") {
      const saved = route.request().postDataJSON();
      settings = settings.map(item => item.id === saved.id ? saved : item);
      await route.fulfill({ json: saved });
    } else {
      await route.fulfill({ json: settings });
    }
  });
}

test("AI setup discovers, saves and clears the default model without losing saved selections", async ({ page }, testInfo) => {
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  page.on("console", message => { if (message.type() === "error") errors.push(message.text()); });
  await mockSettings(page);
  await page.route("**/api/ai-settings/setup-codex/models/discover", route => route.fulfill({ status: 202, json: {
    aiSettingsId: codex.id, jobName: "setup-job", status: "Running", models: []
  } }));
  await page.route("**/api/ai-settings/setup-codex/models/discover/setup-job", route => route.fulfill({ json: {
    aiSettingsId: codex.id, status: "Succeeded", models: [{ id: "discovered-model", displayName: "Discovered model", isDefault: true }]
  } }));
  await page.goto("/settings");
  const model = page.getByRole("combobox", { name: "Default Model", exact: true });
  await expect(model).toHaveValue("saved-model");
  await page.getByRole("button", { name: "Discover / refresh models" }).click();
  await expect(page.getByRole("button", { name: "Discovering models…" })).toBeVisible();
  await expect(model).toBeDisabled();
  await expect(model.locator("option", { hasText: "Discovered model (CLI default)" })).toHaveCount(1);
  await expect(model).toHaveValue("saved-model");
  await model.selectOption("discovered-model");
  await page.screenshot({ path: testInfo.outputPath("ai-default-model.png"), fullPage: true });
  const saved = page.waitForRequest(request => request.url().endsWith("/api/ai-settings") && request.method() === "PUT");
  await page.getByRole("button", { name: "Save AI", exact: true }).click();
  expect((await saved).postDataJSON().model).toBe("discovered-model");
  await expect(page.getByText("Saved. New workflow executions use the first configured AI.")).toBeVisible();
  await page.reload();
  await expect(model).toHaveValue("discovered-model");
  await model.selectOption("");
  const cleared = page.waitForRequest(request => request.url().endsWith("/api/ai-settings") && request.method() === "PUT");
  await page.getByRole("button", { name: "Save AI", exact: true }).click();
  expect((await cleared).postDataJSON().model).toBeNull();
  await expect(page.getByText("Saved. New workflow executions use the first configured AI.")).toBeVisible();
  await page.reload();
  await expect(model).toHaveValue("");
  expect(errors).toEqual([]);
});

test("AI setup retains its model on failed or empty discovery and explains unavailable discovery", async ({ page }) => {
  await mockSettings(page);
  let fail = true;
  await page.route("**/api/ai-settings/setup-codex/models/discover", route => route.fulfill({ json: {
    aiSettingsId: codex.id, status: fail ? "Failed" : "Succeeded", models: [], failureReason: fail ? "Authentication unavailable. Retry." : null
  } }));
  await page.goto("/settings");
  const model = page.getByRole("combobox", { name: "Default Model", exact: true });
  await expect(model).toHaveValue("saved-model");
  await page.getByRole("button", { name: "Discover / refresh models" }).click();
  await expect(page.getByRole("alert")).toContainText("Authentication unavailable");
  await expect(model).toHaveValue("saved-model");
  fail = false;
  await page.getByRole("button", { name: "Discover / refresh models" }).click();
  await expect(page.getByText("The CLI returned no models.")).toBeVisible();
  await expect(page.getByRole("alert")).toHaveCount(0);
  await expect(model).toHaveValue("saved-model");
  await page.getByRole("combobox", { name: "ACP Agent", exact: true }).selectOption("ClaudeCode");
  await expect(page.getByText("Save the runtime and authentication settings before discovering models.")).toBeVisible();
  await page.getByRole("button", { name: /Setup Other/ }).click();
  await expect(model).toHaveValue("other-model");
  await expect(page.getByText("CLI model discovery is not supported for this configuration.")).toBeVisible();
  await page.getByRole("button", { name: "New", exact: true }).click();
  await expect(model).toHaveValue("");
  await expect(page.getByText("Save this AI before discovering models.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Discover / refresh models" })).toHaveCount(0);
});

test("AI setup ignores discovery results after switching configurations", async ({ page }) => {
  await mockSettings(page);
  let release!: () => void;
  const pending = new Promise<void>(resolve => { release = resolve; });
  await page.route("**/api/ai-settings/setup-codex/models/discover", async route => {
    await pending;
    await route.fulfill({ json: { aiSettingsId: codex.id, status: "Succeeded", models: [{ id: "stale-model", displayName: "Stale model", isDefault: false }] } });
  });
  await page.goto("/settings");
  await page.getByRole("button", { name: "Discover / refresh models" }).click();
  await expect(page.getByRole("button", { name: "Discovering models…" })).toBeVisible();
  await page.getByRole("button", { name: /Setup Other/ }).click();
  const completed = page.waitForResponse(response => response.url().endsWith("/setup-codex/models/discover"));
  release();
  await completed;
  const model = page.getByRole("combobox", { name: "Default Model", exact: true });
  await expect(model).toHaveValue("other-model");
  await expect(model).toBeEnabled();
  await expect(model.locator("option", { hasText: "Stale model" })).toHaveCount(0);
  await page.getByRole("button", { name: /Setup Codex/ }).click();
  await expect(model).toHaveValue("saved-model");
  await expect(model.locator("option", { hasText: "Stale model" })).toHaveCount(0);
});
