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
