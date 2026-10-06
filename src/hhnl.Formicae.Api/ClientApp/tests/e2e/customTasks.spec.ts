import { expect, test, type Page, type APIRequestContext } from "@playwright/test";
const api = "http://127.0.0.1:5000";
async function task(request: APIRequestContext, name = "Summarizer") {
  const response = await request.post(`${api}/api/custom-tasks`, { data: { name: `${name} ${Date.now()}`, description: "Review supplied context", promptTemplate: "Summarize {{input.topic}} in {{input.count}} lines. Strict: {{input.strict}}", inputs: [{ name: "topic", valueType: "string", required: true }, { name: "count", valueType: "number", required: false, defaultValue: 3 }, { name: "strict", valueType: "boolean", required: false, defaultValue: false }], runner: { kind: "agent", timeoutSeconds: 90 } } });
  expect(response.ok()).toBeTruthy(); return response.json();
}
async function definition(request: APIRequestContext, taskId: string) {
  const item = await (await request.post(`${api}/api/workflow-definitions`, { data: { name: `Custom workflow ${Date.now()}` } })).json();
  const response = await request.post(`${api}/api/workflow-definitions/${item.id}/versions`, { data: { isEnabled: true, isDefault: false, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "custom", steps: [{ id: "custom", uses: "builtins.custom-task", displayName: "Summarize", customTask: { taskId, inputs: { topic: "Evidence" } } }] } } });
  expect(response.ok(), await response.text()).toBeTruthy(); return item;
}
async function open(page: Page, name: string) { await page.goto("/workflow-definitions"); await page.locator(".editor-workflow-name").click(); await page.getByRole("complementary", { name: "Choose workflow" }).getByRole("button", { name, exact: true }).click(); await expect(page.locator(".editor-save-status")).toHaveText("Saved"); await find(page, "custom"); }
async function find(page: Page, id: string) { await page.getByLabel("Find a node").fill(id); await page.locator(".editor-search-results").getByRole("button", { name: new RegExp(`\\(${id}\\)$`) }).click(); }
async function latest(request: APIRequestContext, id: string) { return (await (await request.get(`${api}/api/workflow-definitions/${id}`)).json()).versions[0].definition; }

test("custom catalog edits typed schema with conflict protection and soft deletion", async ({ page, request }, testInfo) => {
  await page.goto("/custom-tasks"); await page.getByRole("button", { name: "New custom task", exact: true }).click();
  const name = `Catalog task ${Date.now()}`;
  await page.getByLabel("Task name", { exact: true }).fill(name); await page.getByLabel("Prompt template", { exact: true }).fill("Evaluate {{input.count}}");
  await page.getByRole("button", { name: "Add input", exact: true }).click(); await page.getByLabel("Input 1 name", { exact: true }).fill("count"); await page.getByLabel("Input 1 type", { exact: true }).selectOption("number"); await page.getByLabel("Input 1 has default", { exact: true }).check(); await page.getByLabel("Input 1 default", { exact: true }).fill("5");
  await page.getByLabel("Timeout seconds", { exact: true }).fill("120"); await page.getByRole("button", { name: "Save custom task", exact: true }).click(); await expect(page.getByText("Custom task saved.", { exact: true })).toBeVisible();
  const saved = (await (await request.get(`${api}/api/custom-tasks`)).json()).find((item: {name: string}) => item.name === name); expect(saved.inputs[0].defaultValue).toBe(5); expect(saved.runner.timeoutSeconds).toBe(120);
  expect((await request.put(`${api}/api/custom-tasks/${saved.id}`, { data: { ...saved, expectedRevision: saved.revision, description: "External revision" } })).ok()).toBeTruthy();
  await page.getByLabel("Description", { exact: true }).fill("Local unsaved description"); await page.getByRole("button", { name: "Save custom task", exact: true }).click(); await expect(page.getByText(/Your edits are retained/)).toBeVisible();
  await page.route("**/api/custom-tasks", route => route.fulfill({ status: 503, json: { error: "Catalog unavailable" } })); await page.getByRole("button", { name: "Reload current revision", exact: true }).click(); await page.getByRole("dialog").getByRole("button", { name: "Discard", exact: true }).click(); await expect(page.getByLabel("Description", { exact: true })).toHaveValue("Local unsaved description"); await page.unroute("**/api/custom-tasks");
  await page.getByRole("button", { name: "Reload current revision", exact: true }).click(); await page.getByRole("dialog").getByRole("button", { name: "Discard", exact: true }).click(); await expect(page.getByLabel("Description", { exact: true })).toHaveValue("External revision");
  for (const width of [1600,800]) { await page.setViewportSize({ width, height: 900 }); if (width === 800) await expect.poll(async () => page.locator(".side-nav").evaluate(el => el.getBoundingClientRect().right)).toBeLessThanOrEqual(1); await page.evaluate(() => window.scrollTo(0,0)); await page.screenshot({ path: testInfo.outputPath(`custom-tasks-${width}.png`), fullPage: true }); }
  await page.getByRole("button", { name: "Delete custom task", exact: true }).click(); await page.screenshot({ path: testInfo.outputPath("custom-task-delete.png") }); await page.getByRole("dialog").getByRole("button", { name: "Delete", exact: true }).click(); await expect(page.getByText(/Custom task deleted/)).toBeVisible();
});

test("custom nodes persist typed values revisions duplication and saved previews across undo", async ({ page, request }, testInfo) => {
  const custom = await task(request), item = await definition(request, custom.id); await open(page, item.name);
  await expect(page.getByLabel("Step model")).toHaveValue(""); await page.getByLabel("Step persona", {exact:true}).selectOption("default");
  await expect(page.getByLabel("Value for topic", { exact: true })).toHaveValue("Evidence"); await page.getByLabel("Provide count", { exact: true }).check(); await page.getByLabel("Value for count", { exact: true }).fill(""); await page.getByLabel("Value for count", { exact: true }).blur(); await expect(page.getByLabel("Value for count", { exact: true })).toHaveValue(""); await expect(page.getByText(/Enter a finite number/)).toBeVisible(); await page.getByLabel("Value for count", { exact: true }).fill("9007199254740993"); await expect(page.getByText(/Enter a finite number/)).toBeVisible(); await page.getByLabel("Value for count", { exact: true }).fill("1e-100"); await expect(page.getByText(/at most 28 decimal places/).first()).toBeVisible(); await expect(page.getByLabel("Value for count", {exact:true})).toHaveAttribute("aria-invalid", "true"); await page.getByLabel("Value for count", { exact: true }).fill("-2.5"); await page.getByLabel("Provide strict", { exact: true }).check(); await page.getByLabel("Value for strict", { exact: true }).selectOption("true");
  await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.locator(".editor-save-status")).toHaveText("Saved"); expect((await latest(request,item.id)).steps[0].customTask.inputs).toEqual({topic:"Evidence",count:-2.5,strict:true});
  expect((await request.put(`${api}/api/custom-tasks/${custom.id}`, { data: { ...custom, expectedRevision: 1, description: "Revised instructions" } })).ok()).toBeTruthy(); await page.getByRole("button", { name: "Refresh", exact: true }).click(); await expect(page.getByText("Saved version uses task revision 1; Save Version will use revision 2.")).toBeVisible();
  await page.getByText("Template for next save · revision 2", { exact: true }).click(); await page.getByText("Saved version uses task revision 1; Save Version will use revision 2.").scrollIntoViewIfNeeded(); await page.screenshot({ path: testInfo.outputPath("custom-task-revision.png") });
  await page.getByLabel("Display Name", { exact: true }).fill("Revised task"); await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.locator(".editor-save-status")).toHaveText("Saved"); await page.getByRole("button", { name: "Undo", exact: true }).click(); await expect(page.getByText(`Saved task: ${custom.name} · revision 2`, { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Duplicate task", exact: true }).click(); await expect(page.getByLabel("Task definition", { exact: true })).toHaveValue(custom.id); await expect(page.getByLabel("Value for strict", { exact: true })).toHaveValue("true"); await expect(page.getByLabel("Next step", { exact: true })).toHaveValue("");
  await page.getByLabel("Task", {exact:true}).selectOption("builtins.plan"); await expect(page.getByLabel("Task definition", { exact: true })).toHaveCount(0); await page.getByRole("button", { name: "Undo", exact: true }).click(); await expect(page.getByLabel("Task definition", { exact: true })).toHaveValue(custom.id);
});

test("deleted custom tasks retain saved snapshots while disabled drafts keep references", async ({ page, request }) => {
  const custom = await task(request), item = await definition(request,custom.id); expect((await request.delete(`${api}/api/custom-tasks/${custom.id}?expectedRevision=1`)).ok()).toBeTruthy(); await open(page,item.name);
  await expect(page.getByText(/saved version remains runnable with its recorded task/)).toBeVisible(); await page.getByRole("button", { name: "Workflow settings", exact: true }).click(); await page.getByLabel("Enabled", { exact: true }).uncheck(); await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.locator(".editor-save-status")).toHaveText("Saved"); expect((await latest(request,item.id)).steps[0].customTask.taskId).toBe(custom.id);
});

test("custom catalog and node configuration are read-only for viewers", async ({page,request}) => {
  const custom = await task(request), item=await definition(request,custom.id);
  await page.route("**/api/auth/current-user",async route => { const response=await route.fetch(); const user=await response.json(); await route.fulfill({json:{...user,canAdminister:false,canViewWorkflows:true}}); });
  await page.goto("/custom-tasks"); await page.getByRole("complementary",{name:"Custom task catalog"}).getByRole("button",{name:new RegExp(custom.name)}).click(); await expect(page.getByLabel("Prompt template",{exact:true})).toBeDisabled(); await expect(page.getByRole("button",{name:"Save custom task",exact:true})).toBeDisabled();
  await open(page,item.name); await expect(page.getByLabel("Task definition",{exact:true})).toBeDisabled(); await expect(page.getByLabel("Provide count",{exact:true})).toBeDisabled();
});

test("delayed custom snapshot saves retain newer task selections and input edits", async ({page,request}) => {
  const first=await task(request,"First"), second=await task(request,"Second"), item=await definition(request,first.id); await open(page,item.name); await page.getByLabel("Value for topic",{exact:true}).fill("Submitted input");
  await page.route(`**/api/workflow-definitions/${item.id}/versions`, route=>route.fulfill({status:503,json:{error:"Version save unavailable"}})); await page.getByRole("button",{name:"Save Version",exact:true}).click(); await expect(page.getByText("Version save unavailable",{exact:true})).toBeVisible(); await expect(page.getByLabel("Value for topic",{exact:true})).toHaveValue("Submitted input"); await page.unroute(`**/api/workflow-definitions/${item.id}/versions`);
  let release!:()=>void;const gate=new Promise<void>(resolve=>{release=resolve;});await page.route(`**/api/workflow-definitions/${item.id}/versions`,async route=>{await gate;await route.continue();});
  await page.getByRole("button",{name:"Save Version",exact:true}).click(); await expect(page.getByRole("button",{name:"Save Version",exact:true})).toBeDisabled(); await page.getByLabel("Task definition",{exact:true}).selectOption(second.id); await page.getByLabel("Provide topic",{exact:true}).check(); await page.getByLabel("Value for topic",{exact:true}).fill("Later input"); release(); await expect(page.getByRole("button",{name:"Save Version",exact:true})).toBeEnabled(); await expect(page.locator(".editor-save-status")).toHaveText("Unsaved changes"); await expect(page.getByLabel("Task definition",{exact:true})).toHaveValue(second.id); await expect(page.getByLabel("Value for topic",{exact:true})).toHaveValue("Later input"); expect((await latest(request,item.id)).steps[0].customTask.snapshot.id).toBe(first.id);
});

test("custom history displays captured inputs final output and missing metadata", async ({page},testInfo) => {
  const id="11111111-1111-1111-1111-111111111111", timestamp="2026-09-06T10:00:00Z";const workflow={workflowId:id,issueUrl:"https://example.com/issues/1",repositoryUrl:"https://example.com/repo",status:8,currentStep:6,createdAt:timestamp,updatedAt:timestamp};
  await page.route("**/api/workflows**",route=>{const path=new URL(route.request().url()).pathname;const json=path.endsWith("/runs")?[{id:"run1",kind:4,status:"Succeeded",definitionStepId:"summary",updatedAt:timestamp,agentMessages:[],output:"Final summary",customTaskExecution:{taskId:"deleted",revision:3,name:"Recorded summarizer",inputs:{topic:"Captured"},workflowFields:{model:"saved-model"},timeoutSeconds:90,prompt:"Prepared once",formatVersion:1}},{id:"run2",kind:4,status:"Succeeded",updatedAt:timestamp,agentMessages:[],output:"",customTaskExecution:null}]:path==="/api/workflows"?[workflow]:path===`/api/workflows/${id}`?workflow:[];return route.fulfill({json});});
  await page.goto("/workflows");await expect(page.getByText("Recorded summarizer · revision 3",{exact:true})).toBeVisible();await page.getByRole("button",{name:"Expand Prepared inputs",exact:true}).click();await expect(page.locator("pre").filter({hasText:'"topic": "Captured"'})).toBeVisible();await page.getByRole("button",{name:"Expand Task output",exact:true}).first().click();await expect(page.getByText("Final summary",{exact:true})).toBeVisible();await expect(page.getByText("Custom task metadata unavailable",{exact:true})).toBeVisible();await page.screenshot({path:testInfo.outputPath("custom-task-history.png"),fullPage:true});
});

test("output schema editing and data connections support undo save reload deletion and stale errors", async ({ page, request }, testInfo) => {
  test.setTimeout(60_000);
  const name = `Output producer ${Date.now()}`;
  await page.goto("/custom-tasks"); await page.getByRole("button", { name: "New custom task", exact: true }).click();
  await page.getByLabel("Task name", { exact: true }).fill(name); await page.getByLabel("Prompt template", { exact: true }).fill("Return a summary containing ready.");
  await page.getByRole("button", { name: "Add output", exact: true }).click(); await page.getByLabel("Output 1 name", { exact: true }).fill("summary"); await page.getByLabel("Output 1 required", { exact: true }).check();
  await page.getByRole("button", { name: "Save custom task", exact: true }).click(); await expect(page.getByText("Custom task saved.", { exact: true })).toBeVisible();
  const producer = (await (await request.get(`${api}/api/custom-tasks`)).json()).find((item: { name: string }) => item.name === name);
  expect(producer.outputs).toEqual([{ name: "summary", valueType: "string", required: true }]);
  const consumer = await (await request.post(`${api}/api/custom-tasks`, { data: { name: `Output consumer ${Date.now()}`, promptTemplate: "Use {{input.summary}}", inputs: [{ name: "summary", valueType: "string", required: true }] } })).json();
  const item = await (await request.post(`${api}/api/workflow-definitions`, { data: { name: `Data connections ${Date.now()}` } })).json();
  const saved = await request.post(`${api}/api/workflow-definitions/${item.id}/versions`, { data: { isEnabled: true, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "producer", steps: [
    { id: "producer", uses: "builtins.custom-task", displayName: "Producer", nextStepId: "producer2", customTask: { taskId: producer.id } },
    { id: "producer2", uses: "builtins.custom-task", displayName: "Producer 2", nextStepId: "custom", customTask: { taskId: producer.id } },
    { id: "custom", uses: "builtins.custom-task", displayName: "Consumer", customTask: { taskId: consumer.id, inputs: { summary: "literal" } } }
  ] } } }); expect(saved.ok(), await saved.text()).toBeTruthy();
  await open(page, item.name);
  const source = page.getByLabel("Source for summary", { exact: true });
  await source.selectOption({ label: "Producer · summary" }); await expect(source).toHaveValue(JSON.stringify(["producer", "summary"]));
  await expect(page.locator('.react-flow__edge[data-id="data:custom:summary"]')).toHaveCount(1);
  await page.getByRole("button", { name: "Undo", exact: true }).click(); await expect(source).toHaveValue(""); await expect(page.getByLabel("Value for summary", { exact: true })).toHaveValue("literal");
  await page.getByRole("button", { name: "Redo", exact: true }).click(); await expect(source).toHaveValue(JSON.stringify(["producer", "summary"]));
  await page.getByRole("button", { name: "Save Version", exact: true }).click(); await expect(page.locator(".editor-save-status")).toHaveText("Saved");
  let doc = await latest(request, item.id); expect(doc.steps[0].nextStepId).toBe("producer2"); expect(doc.steps[1].nextStepId).toBe("custom"); expect(doc.steps[2].customTask.bindings.summary).toEqual({ stepId: "producer", outputName: "summary" }); expect(doc.steps[2].customTask.inputs).toEqual({});
  await page.reload(); await open(page, item.name); await expect(source).toHaveValue(JSON.stringify(["producer", "summary"]));
  await source.selectOption(""); await expect(page.locator('.react-flow__edge[data-id="data:custom:summary"]')).toHaveCount(0);
  await page.getByRole("button", { name: "Fit All", exact: true }).click();
  await page.locator('.react-flow__node[data-id="producer2"] [data-handleid="output:summary"]').click();
  await page.locator('.react-flow__node[data-id="custom"] [data-handleid="data:summary"]').click();
  await find(page, "custom"); await expect(source).toHaveValue(JSON.stringify(["producer2", "summary"]));
  await page.getByRole("button", { name: "Fit All", exact: true }).click();
  await page.locator('.react-flow__node[data-id="producer"] [data-handleid="output:summary"]').click();
  await page.locator('.react-flow__node[data-id="custom"] [data-handleid="data:summary"]').click();
  await page.getByRole("dialog").getByRole("button", { name: "Replace", exact: true }).click();
  await find(page, "custom"); await expect(source).toHaveValue(JSON.stringify(["producer", "summary"]));
  await find(page, "producer"); await page.getByRole("button", { name: "Delete", exact: true }).click(); await expect(page.locator('.react-flow__edge[data-id="data:custom:summary"]')).toHaveCount(0);
  await page.getByRole("button", { name: "Undo", exact: true }).click(); await find(page, "custom"); await expect(source).toHaveValue(JSON.stringify(["producer", "summary"]));
  expect((await request.put(`${api}/api/custom-tasks/${producer.id}`, { data: { ...producer, expectedRevision: 1, outputs: [{ name: "summary", valueType: "boolean", required: true }] } })).ok()).toBeTruthy();
  await page.getByRole("button", { name: "Refresh", exact: true }).click(); await expect(page.getByText("Binding is stale or the producer is no longer guaranteed to execute first.", { exact: true })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("task-data-stale-binding.png"), fullPage: true });
});

test("reconnecting then deleting a data edge clears destination literals through undo and reload", async ({ page, request }, testInfo) => {
  test.setTimeout(60_000);
  const producer = await (await request.post(`${api}/api/custom-tasks`, { data: { name: `Reconnect producer ${Date.now()}`, promptTemplate: "Return ready", outputs: [{ name: "summary", valueType: "string", required: true }] } })).json();
  const consumer = await (await request.post(`${api}/api/custom-tasks`, { data: { name: `Reconnect consumer ${Date.now()}`, promptTemplate: "Use {{input.summary}}", inputs: [{ name: "summary", valueType: "string", required: false }] } })).json();
  const item = await (await request.post(`${api}/api/workflow-definitions`, { data: { name: `Reconnect data ${Date.now()}` } })).json();
  const saved = await request.post(`${api}/api/workflow-definitions/${item.id}/versions`, { data: { isEnabled: true, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "producer", steps: [
    { id: "producer", uses: "builtins.custom-task", displayName: "Producer", nextStepId: "first", customTask: { taskId: producer.id } },
    { id: "first", uses: "builtins.custom-task", displayName: "First consumer", nextStepId: "custom", customTask: { taskId: consumer.id, bindings: { summary: { stepId: "producer", outputName: "summary" } } } },
    { id: "custom", uses: "builtins.custom-task", displayName: "Second consumer", customTask: { taskId: consumer.id, inputs: { summary: "old literal" } } }
  ] } } }); expect(saved.ok(), await saved.text()).toBeTruthy();
  await open(page, item.name);
  await expect(page.getByLabel("Value for summary", { exact: true })).toHaveValue("old literal");
  await page.getByRole("button", { name: "Fit All", exact: true }).click();
  await page.locator('.react-flow__edge[data-id="data:first:summary"] .react-flow__edgeupdater-target').dragTo(page.locator('.react-flow__node[data-id="custom"] [data-handleid="data:summary"]'));
  const source = page.getByLabel("Source for summary", { exact: true });
  await expect(source).toHaveValue(JSON.stringify(["producer", "summary"]));
  await page.getByRole("button", { name: "Undo", exact: true }).click();
  await expect(source).toHaveValue(""); await expect(page.getByLabel("Value for summary", { exact: true })).toHaveValue("old literal");
  await page.getByRole("button", { name: "Redo", exact: true }).click();
  await expect(source).toHaveValue(JSON.stringify(["producer", "summary"]));
  const edge = page.locator('.react-flow__edge[data-id="data:custom:summary"]');
  await edge.press("Enter");
  await expect(edge).toHaveClass(/selected/);
  await page.getByRole("button", { name: "Delete", exact: true }).click();
  await expect(edge).toHaveCount(0); await find(page, "custom");
  await expect(source).toHaveValue(""); await expect(page.getByLabel("Provide summary", { exact: true })).not.toBeChecked();
  await expect(page.getByLabel("Value for summary", { exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Save Version", exact: true }).click();
  await expect(page.locator(".editor-save-status")).toHaveText("Saved");
  const document = await latest(request, item.id);
  expect(document.steps.find((step: { id: string }) => step.id === "custom").customTask.inputs).toEqual({});
  expect(document.steps.find((step: { id: string }) => step.id === "custom").customTask.bindings).toEqual({});
  await page.reload(); await open(page, item.name);
  await expect(page.getByLabel("Provide summary", { exact: true })).not.toBeChecked();
  await page.screenshot({ path: testInfo.outputPath("reconnected-input-without-literal.png"), fullPage: true });
});

test("producer consumer runtime exposes validated outputs and frozen input provenance", async ({ page, request }, testInfo) => {
  const producer = await (await request.post(`${api}/api/custom-tasks`, { data: { name: `Runtime producer ${Date.now()}`, promptTemplate: "Return ready", outputs: [{ name: "summary", valueType: "string", required: true }] } })).json();
  const consumer = await (await request.post(`${api}/api/custom-tasks`, { data: { name: `Runtime consumer ${Date.now()}`, promptTemplate: "Use {{input.summary}}", inputs: [{ name: "summary", valueType: "string", required: true }] } })).json();
  const item = await (await request.post(`${api}/api/workflow-definitions`, { data: { name: `Runtime data ${Date.now()}` } })).json();
  const response = await request.post(`${api}/api/workflow-definitions/${item.id}/versions`, { data: { isEnabled: true, definition: { schema: "formicae.workflow/v1alpha3", startStepId: "producer", steps: [
    { id: "producer", uses: "builtins.custom-task", nextStepId: "consumer", customTask: { taskId: producer.id } },
    { id: "consumer", uses: "builtins.custom-task", customTask: { taskId: consumer.id, bindings: { summary: { stepId: "producer", outputName: "summary" } } } }
  ] } } }); expect(response.ok(), await response.text()).toBeTruthy(); const version = await response.json();
  const started = await request.post(`${api}/api/workflows/github-issue`, { data: { issueUrl: `https://github.com/example/repo/issues/${Date.now()}`, repositoryUrl: "https://github.com/example/repo", workflowDefinitionVersionId: version.id } }); expect(started.ok(), await started.text()).toBeTruthy(); const workflow = await started.json();
  await expect.poll(async () => (await (await request.get(`${api}/api/workflows/${workflow.workflowId}/runs`)).json()).filter((run: { status: string | number }) => run.status === "Succeeded" || run.status === 2).length, { timeout: 30_000 }).toBe(2);
  const runs = await (await request.get(`${api}/api/workflows/${workflow.workflowId}/runs`)).json(); const source = runs.find((run: { definitionStepId: string }) => run.definitionStepId === "producer"), target = runs.find((run: { definitionStepId: string }) => run.definitionStepId === "consumer");
  expect(source.structuredOutputs).toEqual({ summary: "ready" }); expect(target.customTaskExecution.prompt).toBe("Use ready"); expect(target.customTaskExecution.provenance.summary.runId).toBe(source.id);
  await page.goto("/workflows"); await expect(page.getByText(`${consumer.name} · revision 1`, { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Expand Structured outputs", exact: true }).click(); await expect(page.locator("pre").filter({ hasText: '"summary": "ready"' }).first()).toBeVisible();
  const card = page.locator(".run-card").filter({ hasText: `${consumer.name} · revision 1` }); await card.getByRole("button", { name: "Expand Bound input provenance", exact: true }).click(); await expect(card).toContainText(source.id);
  await page.screenshot({ path: testInfo.outputPath("task-data-history.png"), fullPage: true });
});
