import { expect, test, type Page } from "@playwright/test";

const api = "http://127.0.0.1:5000";
async function open(page: Page, name: string) {
  await page.goto("/workflow-definitions");
  await page.locator(".editor-workflow-name").click();
  await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name, exact: true }).click();
  await expect(page.locator(".editor-save-status")).toHaveText("Saved");
}
async function find(page: Page, id: string) {
  await page.getByLabel("Find a node").fill(id);
  await page.locator(".editor-search-results").getByRole("button", { name: new RegExp(`\\(${id}\\)$`) }).click();
}

test("legacy manual entries become editable start nodes without rewriting the old version", async ({ page, request }) => {
  const name = `Legacy start ${Date.now()}`;
  const definition = await (await request.post(`${api}/api/workflow-definitions`, { data: { name } })).json();
  const original = await (await request.post(`${api}/api/workflow-definitions/${definition.id}/versions`, { data: {
    isEnabled: true, isDefault: false, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "plan",
      steps: [{ id: "plan", uses: "builtins.plan", displayName: "Planning" }] }
  } })).json();
  await open(page, name); await find(page, "manual-start");
  await expect(page.getByLabel("Start type", { exact: true })).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "Start", exact: true })).toBeVisible();
  await expect(page.locator('.react-flow__node[data-id="manual-start"] [data-handleid="input"]')).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Set as Start Step" })).toHaveCount(0);
  await expect(page.getByLabel("Next step", { exact: true })).toHaveValue(JSON.stringify(["plan", "input"]));
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  const saved = await (await request.get(`${api}/api/workflow-definitions/${definition.id}`)).json();
  expect(saved.versions[0].definition.startStepId).toBe("manual-start");
  const manualEvent = saved.versions[0].definition.steps.find((step: { id: string }) => step.id === "manual-start");
  expect(manualEvent.uses).toBe("builtins.start");
  expect(manualEvent.event).toEqual({ enabled: true });
  expect(manualEvent.trigger).toBeFalsy();
  expect(saved.versions.find((version: { id: string }) => version.id === original.id).definition.steps).toHaveLength(1);
  const started = await request.post(`${api}/api/workflows/github-issue`, { data: {
    issueUrl: `https://example.test/issues/start-${Date.now()}`, repositoryUrl: "https://example.test/repo",
    workflowDefinitionId: definition.id, workflowDefinitionVersionId: saved.versions[0].id
  } });
  expect(started.status()).toBe(202);
  const workflow = await started.json();
  await expect.poll(async () => (await (await request.get(`${api}/api/workflows/${workflow.workflowId}`)).json()).status, { timeout: 25_000 }).toBe(5);
  await page.goto(`/workflows?workflowId=${workflow.workflowId}`);
  await expect(page.locator('.execution-graph .react-flow__node[data-id="manual-start"]')).toContainText("Started");
});

test("webhook starts save secret references and remain trigger-only across reload", async ({ page, request }, testInfo) => {
  const name = `Webhook start ${Date.now()}`;
  const definition = await (await request.post(`${api}/api/workflow-definitions`, { data: { name } })).json();
  const created = await request.post(`${api}/api/workflow-definitions/${definition.id}/versions`, { data: {
    isEnabled: true, isDefault: false, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "manual",
      steps: [{ id: "manual", uses: "builtins.start", displayName: "Start", nextStepId: "plan", trigger: { type: "Manual", enabled: true, repositoryIds: [], label: null } },
        { id: "plan", uses: "builtins.plan", displayName: "Planning" }] }
  } });
  expect(created.ok()).toBeTruthy();
  await open(page, name); await find(page, "manual");
  await page.getByRole("button", { name: "Delete", exact: true }).click();
  await page.getByRole("button", { name: "+ Add Step", exact: true }).click();
  await page.getByRole("button", { name: /Webhook Start from an authenticated/ }).click();
  await page.getByLabel("Next step", { exact: true }).selectOption(JSON.stringify(["plan", "input"]));
  await page.getByLabel("Webhook secret name", { exact: true }).fill("smoke");
  await expect(page.getByRole("combobox", { name: "Step model" })).toHaveCount(0);
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
  const saved = await (await request.get(`${api}/api/workflow-definitions/${definition.id}`)).json();
  expect(saved.versions[0].definition.startStepId).toBe("");
  expect(saved.versions[0].definition.steps.find((step: { uses: string }) => step.uses === "builtins.webhook").event.webhookSecretName).toBe("smoke");
  await page.reload(); await open(page, name); await find(page, "step2");
  await expect(page.getByLabel("Start type", { exact: true })).toHaveCount(0);
  await expect(page.getByLabel("Webhook secret name", { exact: true })).toHaveValue("smoke");
  await expect(page.getByText(`Webhook path: /api/webhooks/workflows/${saved.versions[0].id}/step2`)).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("webhook-start.png"), fullPage: true });
  expect((await request.post(`${api}/api/workflows/github-issue`, { data: {
    issueUrl: `https://example.test/issues/webhook-${Date.now()}`, repositoryUrl: "https://example.test/repo",
    workflowDefinitionId: definition.id, workflowDefinitionVersionId: saved.versions[0].id
  } })).status()).toBe(400);
});

test("start nodes cannot be connection targets or selected as task data producers", async ({ page, request }) => {
  const name = `Start ports ${Date.now()}`;
  const definition = await (await request.post(`${api}/api/workflow-definitions`, { data: { name } })).json();
  await request.post(`${api}/api/workflow-definitions/${definition.id}/versions`, { data: {
    isEnabled: true, isDefault: false, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "manual",
      steps: [{ id: "manual", uses: "builtins.start", displayName: "Manual start", nextStepId: "plan", trigger: { type: "Manual", enabled: true, repositoryIds: [], label: null } },
        { id: "plan", uses: "builtins.plan", displayName: "Planning" }] }
  } });
  await open(page, name); await find(page, "plan");
  await expect(page.getByLabel("Next step", { exact: true }).locator('option', { hasText: "Manual start" })).toHaveCount(0);
  await find(page, "manual");
  await expect(page.getByRole("button", { name: "Duplicate task", exact: true })).toBeDisabled();
  await expect(page.locator('.react-flow__node[data-id="manual"] [data-handleid^="output:"]')).toHaveCount(0);
});

for (const eventType of ["GitHub: Issue created", "GitHub: Label added", "Gitea: Label added"]) {
  test(`${eventType} is a separate event with its own settings`, async ({ page, request }, testInfo) => {
    const name = `Event catalog ${Date.now()}`;
    const definition = await (await request.post(`${api}/api/workflow-definitions`, { data: { name } })).json();
    await request.post(`${api}/api/workflow-definitions/${definition.id}/versions`, { data: {
      isEnabled: true, isDefault: false, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "plan", steps: [{ id: "plan", uses: "builtins.plan", displayName: "Planning" }] }
    } });
    await open(page, name);
    await page.getByRole("button", { name: "+ Add Step", exact: true }).click();
    const menu = page.getByRole("complementary", { name: "Add step menu" });
    await expect(menu.getByRole("button", { name: /^Start Start a workflow manually/ })).toBeDisabled();
    await expect(menu.getByRole("button", { name: /Trigger/ })).toHaveCount(0);
    await menu.getByRole("button", { name: new RegExp(`^${eventType} `) }).click();
    await expect(page.getByRole("heading", { name: eventType, exact: true })).toBeVisible();
    await expect(page.getByLabel("Start type", { exact: true })).toHaveCount(0);
    await expect(page.getByLabel("Webhook secret name", { exact: true })).toHaveCount(0);
    await expect(page.getByLabel("Label", { exact: true })).toHaveCount(eventType.includes("Label") ? 1 : 0);
    await expect(page.getByRole("combobox", { name: "Step model", exact: true })).toHaveCount(0);
    await page.getByLabel("Event enabled", { exact: true }).uncheck();
    await page.getByLabel("Next step", { exact: true }).selectOption(JSON.stringify(["plan", "input"]));
    await page.getByRole("button", { name: "Save Version", exact: true }).click();
    await expect(page.getByText("Workflow definition version saved.")).toBeVisible();
    const saved = await (await request.get(`${api}/api/workflow-definitions/${definition.id}`)).json();
    const event = saved.versions[0].definition.steps.find((step: { id: string }) => step.id === "step2");
    expect(event.uses).toBe(eventType === "GitHub: Issue created" ? "github.issue-created" : eventType.startsWith("GitHub") ? "github.label-added" : "gitea.label-added");
    expect(event.event.enabled).toBe(false); expect(event.trigger).toBeFalsy();
    await page.reload(); await open(page, name); await find(page, "step2");
    await expect(page.getByRole("heading", { name: eventType, exact: true })).toBeVisible();
    await expect(page.getByLabel("Event enabled", { exact: true })).not.toBeChecked();
    await page.screenshot({ path: testInfo.outputPath("integration-event.png"), fullPage: true });
  });
}
