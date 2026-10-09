import { expect, test, type Page } from "@playwright/test";

async function openWorkflow(page: Page, name: string) {
  await page.locator(".editor-workflow-name").click();
  await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name, exact: true }).click();
  await expect(page.getByRole("button", { name: "Save Version", exact: true })).toBeEnabled();
}
async function addStep(page: Page, type: string) {
  await page.getByRole("button", { name: "+ Add Step", exact: true }).click();
  await page.getByLabel("Search step types").fill(type);
  await page.getByRole("complementary", { name: "Add step menu" }).getByRole("button", { name: new RegExp(type) }).click();
}
const apiUrl = "http://127.0.0.1:5000";

test("ordinary outputs preserve parallel connections and joins when saved and reloaded", async ({ page, request }, testInfo) => {
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  page.on("console", message => { if (message.type() === "error") errors.push(message.text()); });
  const name = `Task graph ${Date.now()}`;
  const definition = await (await request.post(`${apiUrl}/api/workflow-definitions`, { data: { name } })).json();
  const created = await request.post(`${apiUrl}/api/workflow-definitions/${definition.id}/versions`, { data: {
    isEnabled: true, isDefault: false, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "start",
      steps: [{ id: "start", uses: "builtins.plan", nextStepId: "a", nextStepIds: ["b"], displayName: "Start" },
        { id: "a", uses: "builtins.plan", nextStepId: "join", displayName: "Branch A" },
        { id: "b", uses: "builtins.plan", nextStepId: "join", displayName: "Branch B" },
        { id: "join", uses: "builtins.plan", displayName: "Join" }] }
  } });
  expect(created.ok()).toBe(true);
  await page.goto("/workflow-definitions"); await openWorkflow(page, name);
  await expect(page.locator(".react-flow__edge")).toHaveCount(5);
  await page.locator('.react-flow__node[data-id="start"]').click();
  await expect(page.getByRole("button", { name: "Disconnect a", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Disconnect b", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Disconnect b", exact: true }).click();
  await expect(page.locator(".react-flow__edge")).toHaveCount(4);
  await page.getByRole("combobox", { name: "Next step", exact: true }).selectOption(JSON.stringify(["b", "input"]));
  await expect(page.locator(".react-flow__edge")).toHaveCount(5);
  await expect(page.getByRole("button", { name: "Replace", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  await page.reload(); await openWorkflow(page, name);
  await expect(page.locator(".react-flow__edge")).toHaveCount(5);
  const saved = await (await request.get(`${apiUrl}/api/workflow-definitions/${definition.id}`)).json();
  const document = saved.versions[0].definition;
  expect(document.steps.find((step: { id: string }) => step.id === "start").nextStepIds).toEqual(["b"]);
  expect(document.steps.filter((step: { nextStepId: string }) => step.nextStepId === "join")).toHaveLength(2);
  const started = await request.post(`${apiUrl}/api/workflows/github-issue`, { data: {
    issueUrl: `https://example.test/issues/${Date.now()}`, repositoryUrl: "https://example.test/repository",
    workflowDefinitionId: definition.id, workflowDefinitionVersionId: saved.versions[0].id
  } });
  expect(started.ok()).toBe(true);
  const workflow = await started.json();
  await expect.poll(async () => (await (await request.get(`${apiUrl}/api/workflows/${workflow.workflowId}`)).json()).status,
    { timeout: 25_000 }).toBe(5);
  const runs = await (await request.get(`${apiUrl}/api/workflows/${workflow.workflowId}/runs`)).json();
  expect(runs).toHaveLength(4);
  const joined = runs.find((run: { definitionStepId: string }) => run.definitionStepId === "join");
  for (const run of runs.filter((run: { definitionStepId: string }) => ["a", "b"].includes(run.definitionStepId))) {
    expect(run.status).toBe(2);
    expect(Date.parse(run.completedAt)).toBeLessThanOrEqual(Date.parse(joined.startedAt));
  }
  await page.screenshot({ path: testInfo.outputPath("task-graph.png"), fullPage: true });
  expect(errors).toEqual([]);
});

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
  await openWorkflow(page, name);
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
  await openWorkflow(page, name);
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

test("UI loads and navigates between primary pages", async ({ page }, testInfo) => {
  await page.goto("/workflows");
  await expect(page.getByRole("heading", { level: 1, name: "Workflow Management" })).toBeVisible();

  await page.screenshot({ path: testInfo.outputPath("content-workflows.png"), fullPage: true });
  for (const [label, route] of [["Integrations", "integrations"], ["Repositories", "repositories"], ["Users", "users"], ["Settings", "settings"]]) {
    await page.getByRole("button", { name: label, exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/${route}$`));
    await expect(page.locator(".content-header h1")).toBeVisible();
    await page.screenshot({ path: testInfo.outputPath(`content-${route}.png`), fullPage: true });
  }
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

  const historyReady = page.waitForResponse(response => new URL(response.url()).pathname === "/api/workflows/search" && response.ok());
  await page.goto("/workflows");
  await expect(page.getByRole("heading", { level: 1, name: "Workflow Management" })).toBeVisible();
  await historyReady;
  await expect(page.getByRole("region", { name: "Workflow history" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Start Workflow", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Definitions", exact: true }).click();
  await page.getByRole("button", { name: "Manual Start", exact: true }).click();
  await expect(page.getByRole("button", { name: "Start Workflow", exact: true })).toBeEnabled();

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
  await openWorkflow(page, name);
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
  await openWorkflow(page, name);
  await page.locator('.react-flow__node[data-id="loop-planning"]').click();
  await expect(page.getByLabel("Repeat count")).toHaveValue("3");
  const persisted = await (await request.get(`${apiUrl}/api/workflow-definitions/${definition.id}`)).json();
  expect(persisted.versions[0].definition.schema).toBe("formicae.workflow/v1alpha3");
  expect(persisted.versions[0].definition.steps.find((step: { id: string }) => step.id === "loop-planning").loop.repeatCount).toBe(3);
  expect(persisted.versions[0].definition.steps.find((step: { id: string }) => step.id === "trigger-ready").nextStepId).toBe("loop-planning");
  expect(persisted.versions[0].definition.steps.find((step: { id: string }) => step.id === "trigger-ready").event.label).toBe("ready");
  expect(persisted.versions[1].definition.triggers[0].label).toBe("ready");
  expect(persisted.versions[1].definition.schema).toBe("formicae.workflow/v1alpha2");
  expect(persisted.versions[1].definition.loops[0].repeatCount).toBe(2);
});


test("trigger and loop nodes can be created configured connected and deleted", async ({ page, request }) => {
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  page.on("console", message => { if (message.type() === "error") errors.push(message.text()); });
  const name = `Control nodes ${Date.now()}`;
  await page.goto("/workflow-definitions");
  await page.locator(".editor-workflow-name").click();
  await page.getByRole("button", { name: "New Definition", exact: true }).click();
  await page.getByLabel("Definition Name", { exact: true }).fill(name);
  await addStep(page, "Loop");
  await page.locator('.react-flow__node[data-id="step5"]').click();
  await page.getByLabel("Display Name", { exact: true }).fill("Repeat planning");
  await page.getByRole("combobox", { name: "Loop body", exact: true }).selectOption(JSON.stringify(["plan", "input"]));
  await page.getByRole("combobox", { name: "Loop exit", exact: true }).selectOption(JSON.stringify(["implement", "input"]));
  await page.getByRole("button", { name: "Fit All", exact: true }).click();
  await page.locator('.react-flow__node[data-id="manual-start"]').click();
  await page.getByRole("combobox", { name: "Next step", exact: true }).selectOption(JSON.stringify(["step5", "input"]));
  await page.getByRole("button", { name: "Replace", exact: true }).click();
  await page.getByRole("button", { name: "Fit All", exact: true }).click();
  await page.locator('.react-flow__node[data-id="plan"]').click();
  await page.getByRole("combobox", { name: "Next step", exact: true }).selectOption(JSON.stringify(["step5", "return"]));
  await page.getByRole("button", { name: "Replace", exact: true }).click();
  await addStep(page, "GitHub: Label added");
  await page.locator('.react-flow__node[data-id="step6"]').click();
  await page.getByLabel("Display Name", { exact: true }).fill("Issue ready");
  await page.getByLabel("Event enabled", { exact: true }).uncheck();
  await page.getByLabel("Label", { exact: true }).fill("ready");
  await page.getByRole("combobox", { name: "Next step", exact: true }).selectOption(JSON.stringify(["step5", "input"]));
  await expect(page.getByRole("combobox", { name: "Step model", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  await page.reload();
  await openWorkflow(page, name);
  await page.locator('.react-flow__node[data-id="step6"]').click();
  await expect(page.getByLabel("Label", { exact: true })).toHaveValue("ready");
  await expect(page.getByRole("combobox", { name: "Next step", exact: true })).toHaveValue(JSON.stringify(["step5", "input"]));
  await page.locator('.editor-canvas').scrollIntoViewIfNeeded();
  await page.screenshot({ path: test.info().outputPath("control-nodes.png"), fullPage: true });
  await page.getByRole("button", { name: "Delete", exact: true }).click();
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

test("Manual Start uses only the selected definition's saved enabled versions", async ({ page, request }) => {
  const name = `Manual start layout ${Date.now()}`;
  const definition = await (await request.post(`${apiUrl}/api/workflow-definitions`, { data: { name } })).json();
  const document = { schema: "formicae.workflow/v1alpha3", startStepId: "start", steps: [
    { id: "start", uses: "builtins.start", displayName: "Start", event: { type: "builtins.start", enabled: true }, nextStepId: "plan" },
    { id: "plan", uses: "builtins.plan", displayName: "Plan" }
  ] };
  const enabled = await request.post(`${apiUrl}/api/workflow-definitions/${definition.id}/versions`, { data: { isEnabled: true, isDefault: false, definition: document } });
  expect(enabled.ok()).toBe(true);
  const version = await enabled.json();
  const disabled = await request.post(`${apiUrl}/api/workflow-definitions/${definition.id}/versions`, { data: { isEnabled: false, isDefault: false, definition: document } });
  expect(disabled.ok()).toBe(true);
  await page.goto("/workflow-definitions"); await openWorkflow(page, name);
  await page.getByRole("button", { name: "Manual Start", exact: true }).click();
  const form = page.getByRole("form", { name: "Manual Start" });
  await expect(form.getByLabel("Version", { exact: true }).locator("option")).toHaveCount(1);
  await expect(form.getByLabel("Version", { exact: true })).toHaveValue(version.id);
  await form.getByLabel("Issue URL", { exact: true }).fill(`https://example.test/issues/${Date.now()}`);
  await form.getByLabel("Repository URL", { exact: true }).fill("https://example.test/repository");
  await form.getByLabel("Base Branch", { exact: true }).fill("work");
  await form.getByLabel("Model", { exact: true }).fill("chosen-model");
  const started = page.waitForRequest(request => request.url().endsWith("/api/workflows/github-issue") && request.method() === "POST");
  await form.getByRole("button", { name: "Start Workflow", exact: true }).click();
  expect((await started).postDataJSON()).toMatchObject({ workflowDefinitionId: definition.id, workflowDefinitionVersionId: version.id, baseBranch: "work", model: "chosen-model" });
  await expect(page).toHaveURL(/\/workflows\?workflowId=/);
  await expect(page.getByRole("region", { name: "Workflow execution", exact: true })).toBeVisible();
});

test("Manual Start explains unavailable versions and preserves command permissions", async ({ page, request }) => {
  const name = `Disabled manual ${Date.now()}`;
  const definition = await (await request.post(`${apiUrl}/api/workflow-definitions`, { data: { name } })).json();
  const created = await request.post(`${apiUrl}/api/workflow-definitions/${definition.id}/versions`, { data: {
    isEnabled: false, isDefault: false, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "plan", steps: [{ id: "plan", uses: "builtins.plan", displayName: "Plan" }] }
  } });
  expect(created.ok()).toBe(true);
  await page.goto("/workflow-definitions"); await openWorkflow(page, name);
  await page.getByRole("button", { name: "Manual Start", exact: true }).click();
  await expect(page.getByText("This definition has no enabled version with a manual Start event.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Start Workflow", exact: true })).toBeDisabled();
  await page.route("**/api/auth/current-user", async route => {
    const response = await route.fetch(); const user = await response.json();
    await route.fulfill({ json: { ...user, canTriggerWorkflows: false } });
  });
  await page.reload();
  await page.getByRole("button", { name: "Manual Start", exact: true }).click();
  await expect(page.getByText("Workflow command permission is required to start a run.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Start Workflow", exact: true })).toBeDisabled();
});

test("execution ports stay separate from content and status in the compact management layout", async ({ page, request }, testInfo) => {
  const definition = await (await request.post(`${apiUrl}/api/workflow-definitions`, { data: { name: `Execution ports ${Date.now()}` } })).json();
  const created = await request.post(`${apiUrl}/api/workflow-definitions/${definition.id}/versions`, { data: {
    isEnabled: true, isDefault: false, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "start", steps: [
      { id: "start", uses: "builtins.start", displayName: "Start", event: { enabled: true }, nextStepId: "plan" },
      { id: "plan", uses: "builtins.plan", displayName: "Plan" }
    ] }
  } });
  expect(created.ok()).toBe(true);
  const version = await created.json();
  const started = await request.post(`${apiUrl}/api/workflows/github-issue`, { data: { issueUrl: `https://example.test/issues/${Date.now()}`, repositoryUrl: "https://example.test/repository", workflowDefinitionId: definition.id, workflowDefinitionVersionId: version.id } });
  expect(started.ok()).toBe(true);
  const workflow = await started.json();
  await page.route(`**/api/workflows/${workflow.workflowId}/execution`, async route => {
    const response = await route.fetch(); const execution = await response.json();
    const start = execution.definition.steps.find((step: { id: string }) => step.id === execution.definition.startStepId);
    start.uses = "github.issue-created"; start.displayName = "GitHub: Issue created with a long descriptive name"; start.event = { type: "github.issue-created", enabled: true };
    await route.fulfill({ json: execution });
  });
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(`/workflows?workflowId=${workflow.workflowId}`);
  const graph = page.locator(".execution-graph");
  await expect(graph.locator(".editor-data-output").filter({ hasText: "Issue id" })).toBeAttached();
  const measurements = await graph.locator(".execution-node").filter({ hasText: "GitHub: Issue created with a long descriptive name" }).evaluate(node => {
    const rect = (selector: string) => { const box = node.querySelector(selector)!.getBoundingClientRect(); return { top: box.top, bottom: box.bottom }; };
    return { title: rect("strong"), summary: rect(".editor-node-summary"), next: rect(".editor-port"), ports: rect(".editor-data-ports"), status: rect(".execution-node-state") };
  });
  expect(measurements.next.top).toBeGreaterThanOrEqual(measurements.summary.bottom);
  expect(measurements.ports.top).toBeGreaterThanOrEqual(measurements.next.bottom);
  expect(measurements.status.top).toBeGreaterThanOrEqual(measurements.ports.bottom);
  const history = page.getByRole("region", { name: "Workflow history" });
  const detail = page.getByRole("region", { name: "Workflow execution", exact: true });
  expect((await history.boundingBox())!.x + (await history.boundingBox())!.width).toBeLessThan((await detail.boundingBox())!.x);
  await history.locator("summary").filter({ hasText: "Filters and saved views" }).click();
  const filtered = page.waitForRequest(request => request.url().includes("/api/workflows/search?") && request.url().includes("status=Failed"));
  await history.getByLabel("Workflow status", { exact: true }).selectOption("Failed"); await filtered;
  await expect(detail).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("workflow-management-desktop.png"), fullPage: true });
  await page.setViewportSize({ width: 600, height: 900 });
  expect((await detail.boundingBox())!.y).toBeGreaterThan((await history.boundingBox())!.y + (await history.boundingBox())!.height);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath("workflow-management-mobile.png"), fullPage: true });
});
