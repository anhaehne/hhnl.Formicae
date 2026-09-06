import { expect, test } from "@playwright/test";

const apiUrl = "http://127.0.0.1:5000";

test("step model picker discovers through CLI jobs and preserves saved selections", async ({ page, request }) => {
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  page.on("console", message => { if (message.type() === "error") errors.push(message.text()); });
  const name = `Smoke models ${Date.now()}`;
  const definition = await (await request.post(`${apiUrl}/api/workflow-definitions`, { data: { name } })).json();
  const created = await request.post(`${apiUrl}/api/workflow-definitions/${definition.id}/versions`, { data: {
    isEnabled: true, isDefault: false, definition: { schema: "formicae.workflow/v1alpha2", startStepId: "plan",
      steps: [{ id: "plan", uses: "builtins.plan", displayName: "Plan models", aiSettingsId: "codex", model: "saved-model" }] }
  } });
  expect(created.ok()).toBe(true);
  await page.route("**/api/ai-settings", route => route.fulfill({ json: [
    { id: "codex", name: "Codex profile", agentKind: "Acp", acpProvider: "Codex", acpCommand: "codex", authMethod: "CodexSubscription" },
    { id: "other", name: "Other profile", agentKind: "OpenHands", authMethod: "ApiKey" }
  ] }));
  let fail = false;
  await page.route("**/api/ai-settings/codex/models/discover", route => route.fulfill({ status: 202, json: { aiSettingsId: "codex", jobName: "test-job", status: "Running", models: [] } }));
  await page.route("**/api/ai-settings/codex/models/discover/test-job", route => route.fulfill({ json: {
    aiSettingsId: "codex", jobName: "test-job", status: fail ? "Failed" : "Succeeded",
    failureReason: fail ? "Authentication unavailable. Retry." : null,
    models: fail ? [] : [{ id: "discovered-model", displayName: "Discovered model", isDefault: true }]
  } }));
  await page.goto("/workflow-definitions");
  await page.getByRole("button", { name: new RegExp(name) }).click();
  await page.locator('.react-flow__node[data-id="plan"]').click();
  await expect(page.getByRole("combobox", { name: "Step model", exact: true })).toHaveValue("saved-model");
  await expect(page.getByRole("option", { name: "Codex profile", exact: true })).toBeEnabled();
  await page.getByRole("button", { name: "Discover / refresh models" }).click();
  await expect(page.getByRole("button", { name: "Discovering models…" })).toBeVisible();
  await expect(page.getByRole("option", { name: "Discovered model (CLI default)", exact: true })).toBeAttached();
  await expect(page.getByRole("combobox", { name: "Step model", exact: true })).toHaveValue("saved-model");
  await page.getByRole("combobox", { name: "Step model", exact: true }).selectOption("discovered-model");
  await page.getByRole("button", { name: "Save Version" }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  await page.reload();
  await page.getByRole("button", { name: new RegExp(name) }).click();
  await page.locator('.react-flow__node[data-id="plan"]').click();
  await expect(page.getByRole("combobox", { name: "AI configuration", exact: true })).toHaveValue("codex");
  await expect(page.getByRole("combobox", { name: "Step model", exact: true })).toHaveValue("discovered-model");
  fail = true;
  await page.getByRole("button", { name: "Discover / refresh models" }).click();
  await expect(page.getByRole("alert")).toContainText("Authentication unavailable");
  await expect(page.getByRole("combobox", { name: "Step model", exact: true })).toHaveValue("discovered-model");
  await page.getByRole("combobox", { name: "AI configuration", exact: true }).selectOption("other");
  await expect(page.getByRole("combobox", { name: "Step model", exact: true })).toHaveValue("");
  await expect(page.getByText("CLI model discovery is not supported for this configuration.")).toBeVisible();
  expect(errors).toEqual([]);
});

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
        loops: [{ id: "planning", bodyStepIds: ["plan"], repeatCount: 2, maxIterations: 3, timeoutSeconds: 60, exitStepId: "exit" }],
        triggers: [{ id: "ready", type: "DevOpsIssueLabel", enabled: false, repositoryIds: [], label: "ready" }]
      }
    }
  });
  expect(versionResponse.ok()).toBe(true);

  await page.goto("/workflow-definitions");
  await page.getByRole("button", { name: new RegExp(name) }).click();
  await page.locator('.react-flow__node[data-id="loop-planning"]').click();
  await expect(page.getByRole("combobox", { name: "Loop body", exact: true })).toHaveValue(JSON.stringify(["plan", "input"]));
  await expect(page.getByLabel("Repeat count")).toHaveValue("2");
  await expect(page.getByLabel("Maximum iterations")).toHaveValue("3");
  await expect(page.getByLabel("Timeout seconds (optional)")).toHaveValue("60");
  await expect(page.getByRole("combobox", { name: "Loop exit", exact: true })).toHaveValue(JSON.stringify(["exit", "input"]));

  await page.getByLabel("Repeat count").fill("3");
  await page.getByRole("button", { name: "Save Version" }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  await page.reload();
  await page.getByRole("button", { name: new RegExp(name) }).click();
  await page.locator('.react-flow__node[data-id="loop-planning"]').click();
  await expect(page.getByLabel("Repeat count")).toHaveValue("3");
  const persisted = await (await request.get(`${apiUrl}/api/workflow-definitions/${definition.id}`)).json();
  expect(persisted.versions[0].definition.schema).toBe("formicae.workflow/v1alpha3");
  expect(persisted.versions[0].definition.steps.find((step: { id: string }) => step.id === "loop-planning").loop.repeatCount).toBe(3);
  expect(persisted.versions[0].definition.steps.find((step: { id: string }) => step.id === "trigger-ready").nextStepId).toBe("loop-planning");
  expect(persisted.versions[0].definition.steps.find((step: { id: string }) => step.id === "trigger-ready").trigger.label).toBe("ready");
  expect(persisted.versions[1].definition.schema).toBe("formicae.workflow/v1alpha2");
  expect(persisted.versions[1].definition.loops[0].repeatCount).toBe(2);
});


test("trigger and loop nodes can be created configured connected and deleted", async ({ page, request }) => {
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  page.on("console", message => { if (message.type() === "error") errors.push(message.text()); });
  const name = `Control nodes ${Date.now()}`;
  await page.goto("/workflow-definitions");
  await page.getByRole("button", { name: "New Definition", exact: true }).click();
  await page.getByLabel("Definition Name", { exact: true }).fill(name);
  await page.getByLabel("New step type").selectOption("builtins.loop");
  await page.getByRole("button", { name: "Add Step", exact: true }).click();
  await page.locator('.react-flow__node[data-id="step5"]').click();
  await page.getByLabel("Display Name", { exact: true }).fill("Repeat planning");
  await page.getByRole("combobox", { name: "Loop body", exact: true }).selectOption(JSON.stringify(["plan", "input"]));
  await page.getByRole("combobox", { name: "Loop exit", exact: true }).selectOption(JSON.stringify(["implement", "input"]));
  await page.getByRole("button", { name: "Set as Start Step" }).click();
  await page.locator('.react-flow__node[data-id="plan"]').click();
  await page.getByRole("combobox", { name: "Next step", exact: true }).selectOption(JSON.stringify(["step5", "return"]));
  await page.getByLabel("New step type").selectOption("builtins.trigger");
  await page.getByRole("button", { name: "Add Step", exact: true }).click();
  await page.locator('.react-flow__node[data-id="step6"]').click();
  await page.getByLabel("Display Name", { exact: true }).fill("Issue ready");
  await page.getByLabel("Trigger enabled", { exact: true }).uncheck();
  await page.getByLabel("Label", { exact: true }).fill("ready");
  await page.getByRole("combobox", { name: "Next step", exact: true }).selectOption(JSON.stringify(["step5", "input"]));
  await expect(page.getByRole("combobox", { name: "Step model", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  await page.reload();
  await page.getByRole("button", { name: new RegExp(name) }).click();
  await page.locator('.react-flow__node[data-id="step6"]').click();
  await expect(page.getByLabel("Label", { exact: true })).toHaveValue("ready");
  await expect(page.getByRole("combobox", { name: "Next step", exact: true })).toHaveValue(JSON.stringify(["step5", "input"]));
  await page.locator('.workflow-canvas').scrollIntoViewIfNeeded();
  await page.screenshot({ path: test.info().outputPath("control-nodes.png"), fullPage: true });
  await page.getByRole("button", { name: "Delete Step", exact: true }).click();
  await expect(page.locator('.react-flow__node[data-id="step6"]')).toHaveCount(0);
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  const definitions = await (await request.get(`${apiUrl}/api/workflow-definitions`)).json();
  const saved = definitions.find((item: { name: string }) => item.name === name).versions[0].definition;
  expect(saved.steps.find((step: { id: string }) => step.id === "step5").loop.bodyStepId).toBe("plan");
  expect(saved.steps.find((step: { id: string }) => step.id === "plan").nextStepPort).toBe("return");
  expect(saved.steps.some((step: { uses: string }) => step.uses === "builtins.trigger")).toBe(false);
  expect(errors).toEqual([]);
});
