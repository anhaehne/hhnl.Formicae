import { expect, test } from "@playwright/test";

const api = "http://127.0.0.1:5000";
const integrationId = "4e5a0dc3-03da-461a-89e8-bd30711d4a01";
const repositoryId = "4e5a0dc3-03da-461a-89e8-bd30711d4a02";
const otherRepositoryId = "4e5a0dc3-03da-461a-89e8-bd30711d4a03";

for (const mode of ["selection", "failure", "empty"] as const) {
  test(`Create branch ${mode}: repository-scoped source discovery and saved settings`, async ({ page, request }, testInfo) => {
    const name = `Branch ${mode} ${Date.now()}`;
    const item = await (await request.post(`${api}/api/workflow-definitions`, { data: { name } })).json();
    // The development store has no real GitHub connection; model external repository metadata
    // and version storage at the browser boundary. Backend validation/execution is covered in .NET.
    let saved = { id: "browser-version", version: 1, dslSchemaVersion: "formicae.workflow/v1alpha3", isEnabled: false, isDefault: false,
      definition: { schema: "formicae.workflow/v1alpha3", startStepId: "branch", steps: [{ id: "branch", uses: "github.create-branch", displayName: "Create branch", createBranch: { repositoryId, sourceBranch: "release/stable", branchName: "feature/new" } }] } };
    const detail = await (await request.get(`${api}/api/workflow-definitions/${item.id}`)).json();
    let failed = mode === "failure";
    await page.route("**/api/integrations", route => route.fulfill({ json: [{ id: integrationId, providerType: "GitHub", displayName: "GitHub" }] }));
    await page.route(`**/api/integrations/${integrationId}`, route => route.fulfill({ json: { id: integrationId, providerType: "GitHub", displayName: "GitHub", repositories: [
      { id: repositoryId, owner: "acme", name: "repo" }, { id: otherRepositoryId, owner: "acme", name: "other" }
    ] } }));
    await page.route(`**/api/integrations/${integrationId}/repositories/*/branches`, route => route.fulfill(failed
      ? { status: 502, json: { error: "GitHub unavailable" } }
      : { json: mode === "empty" ? [] : route.request().url().includes(otherRepositoryId) ? ["develop"] : ["main", "release/stable"] }));
    await page.route("**/api/workflow-definitions", async route => {
      const items = await (await route.fetch()).json();
      await route.fulfill({ json: items.map((entry: { id: string }) => entry.id === item.id ? { ...entry, versions: [saved] } : entry) });
    });
    await page.route(`**/api/workflow-definitions/${item.id}`, route => route.fulfill({ json: { ...detail, versions: [saved] } }));
    await page.route("**/api/workflow-definitions/validate", route => route.fulfill({ json: { isValid: true, errors: [] } }));
    await page.route(`**/api/workflow-definitions/${item.id}/versions`, async route => {
      saved = { ...saved, version: saved.version + 1, definition: route.request().postDataJSON().definition };
      await route.fulfill({ status: 201, json: saved });
    });
    const errors: string[] = []; page.on("pageerror", error => errors.push(error.message));
    await page.goto("/workflow-definitions");
    await page.locator(".editor-workflow-name").click();
    await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name, exact: true }).click();
    await page.getByLabel("Find a node").fill("branch");
    await page.locator(".editor-search-results").getByRole("button", { name: /\(branch\)$/ }).click();
    for (const name of ["repository", "sourceBranch", "targetBranch"]) await expect(page.locator(`[aria-label="${name} input: string"]`)).toBeVisible();
    await expect(page.getByLabel("Step model", { exact: true })).toHaveCount(0);
    await expect(page.getByLabel("New branch name")).toHaveValue("feature/new");
    if (mode === "failure") {
      await expect(page.getByRole("alert").filter({ hasText: "Could not load source branches" })).toBeVisible();
      failed = false; await page.getByRole("button", { name: "Retry loading branches" }).click();
      await expect(page.getByLabel("Source branch")).toBeEnabled();
    } else if (mode === "empty") {
      await expect(page.getByText("This repository has no source branches.")).toBeVisible();
      await expect(page.getByLabel("Source branch").locator('option[value="release/stable"]')).toHaveText("release/stable (unavailable)");
    } else {
      await expect(page.getByLabel("Source branch")).toHaveValue("release/stable");
      await page.getByLabel("Connected GitHub repository").selectOption(otherRepositoryId);
      await expect(page.getByLabel("Source branch")).toHaveValue("");
      await expect(page.getByLabel("Source branch").locator('option[value="develop"]')).toHaveCount(1);
      await page.getByLabel("Source branch").selectOption("develop");
      await page.getByLabel("New branch name").fill("feature/from-develop");
      await page.getByRole("button", { name: "Save Version", exact: true }).click();
      await expect(page.locator(".editor-save-status")).toHaveText("Saved");
      expect(saved.definition.steps[0].createBranch).toEqual({ repositoryId: otherRepositoryId, sourceBranch: "develop", branchName: "feature/from-develop" });
      await page.reload();
      await page.locator(".editor-workflow-name").click();
      await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name, exact: true }).click();
      await page.getByLabel("Find a node").fill("branch");
      await page.locator(".editor-search-results").getByRole("button", { name: /\(branch\)$/ }).click();
      await expect(page.getByLabel("Source branch")).toHaveValue("develop");
      await expect(page.getByLabel("New branch name")).toHaveValue("feature/from-develop");
    }
    await page.getByRole("button", { name: "+ Add Step", exact: true }).click();
    await expect(page.getByRole("complementary", { name: "Add step menu" }).getByRole("button", { name: /GitHub: Create branch/ })).toBeVisible();
    await page.screenshot({ path: testInfo.outputPath(`create-branch-${mode}.png`), fullPage: true });
    expect(errors).toEqual([]);
  });
}

test("New Create branch accepts upstream inputs and retains them through edits and reload", async ({ page, request }, testInfo) => {
  test.setTimeout(90_000);
  const name = `Branch connections ${Date.now()}`;
  const item = await (await request.post(`${api}/api/workflow-definitions`, { data: { name } })).json();
  const seeded = await request.post(`${api}/api/workflow-definitions/${item.id}/versions`, { data: { isEnabled: false, isDefault: false, definition: {
    schema: "formicae.workflow/v1alpha3", startStepId: "repo", steps: [
      { id: "repo", uses: "builtins.template", displayName: "Repository", nextStepId: "source", template: { template: "https://github.com/acme/repo", inputs: [] } },
      { id: "source", uses: "builtins.template", displayName: "Source", nextStepId: "target", template: { template: "main", inputs: [] } },
      { id: "target", uses: "builtins.template", displayName: "Target", template: { template: "feature/input", inputs: [] } }
    ]
  } } });
  expect(seeded.ok(), await seeded.text()).toBeTruthy();
  const errors: string[] = []; page.on("pageerror", error => errors.push(error.message));
  async function open() {
    await page.goto("/workflow-definitions");
    await page.locator(".editor-workflow-name").click();
    await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name, exact: true }).click();
  }
  async function inspect(id: string) {
    await page.getByLabel("Find a node").fill(id);
    await page.locator(".editor-search-results").getByRole("button", { name: new RegExp(`\\(${id}\\)$`) }).click();
  }
  await open(); await inspect("target");
  await page.getByRole("button", { name: "Add after Target Next", exact: true }).click();
  await page.getByRole("complementary", { name: "Add step menu" }).getByRole("button", { name: /GitHub: Create branch/ }).click();
  const branchId = await page.locator('.react-flow__node.selected').getAttribute("data-id");
  expect(branchId).toBeTruthy();
  const bindings = { repository: { stepId: "repo", outputName: "text" }, sourceBranch: { stepId: "source", outputName: "text" }, targetBranch: { stepId: "target", outputName: "text" } };
  for (const [input, binding] of Object.entries(bindings)) {
    await page.getByLabel(`${input} input value`).fill(input === "repository" ? "https://github.com/old/repo" : "old-branch");
    if (input === "targetBranch") {
      await page.getByRole("button", { name: "Fit All", exact: true }).click();
      const source = await page.locator('.react-flow__node[data-id="target"] [data-handleid="output:text"]').boundingBox();
      const target = await page.locator(`.react-flow__node[data-id="${branchId}"] [data-handleid="data:targetBranch"]`).boundingBox();
      expect(source).toBeTruthy(); expect(target).toBeTruthy();
      await page.mouse.move(source!.x + source!.width / 2, source!.y + source!.height / 2); await page.mouse.down();
      await page.mouse.move(target!.x + target!.width / 2, target!.y + target!.height / 2, { steps: 20 }); await page.mouse.up();
      await expect(page.getByLabel(`Source for ${input}`)).toHaveValue(JSON.stringify([binding.stepId, binding.outputName]));
    } else await page.getByLabel(`Source for ${input}`).selectOption(JSON.stringify([binding.stepId, binding.outputName]));
    await expect(page.locator(`.react-flow__edge[data-id="data:${binding.stepId}:text:${branchId}:${input}"]`)).toHaveCount(1);
  }
  // Editing an ordinary property must keep all graph-derived bindings.
  await page.getByLabel("Display Name", { exact: true }).fill("Connected branch");
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.locator(".editor-save-status")).toHaveText("Saved");
  const saved = (await (await request.get(`${api}/api/workflow-definitions/${item.id}`)).json()).versions[0].definition;
  expect(saved.steps.find((step: { id: string }) => step.id === branchId).createBranch).toMatchObject({ bindings, inputs: {} });
  await page.reload(); await open(); await inspect(branchId!);
  for (const [input, binding] of Object.entries(bindings)) await expect(page.getByLabel(`Source for ${input}`)).toHaveValue(JSON.stringify([binding.stepId, binding.outputName]));
  await page.getByLabel("Source for targetBranch").selectOption("");
  await page.getByLabel("targetBranch input value").fill("feature/literal");
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.locator(".editor-save-status")).toHaveText("Saved");
  const changed = (await (await request.get(`${api}/api/workflow-definitions/${item.id}`)).json()).versions[0].definition;
  expect(changed.steps.find((step: { id: string }) => step.id === branchId).createBranch).toMatchObject({ bindings: { repository: bindings.repository, sourceBranch: bindings.sourceBranch }, inputs: { targetBranch: "feature/literal" } });
  expect(changed.steps.find((step: { id: string }) => step.id === branchId).createBranch.bindings.targetBranch).toBeUndefined();
  await page.screenshot({ path: testInfo.outputPath("branch-connected-inputs.png"), fullPage: true });
  expect(errors).toEqual([]);
});
