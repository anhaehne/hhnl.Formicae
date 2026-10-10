# Workflow template node verification

Implementation scope: `builtins.template`, Scriban textarea, unlimited-count named scalar data input ports, plain string `text` output, inline durable execution and retained input provenance.

- `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --configuration Release -m:1 --filter FullyQualifiedName~WorkflowTemplateTests`: 23 cases passed, including 65 inputs, mixed scalar rendering, missing values, syntax, rendering limits, sequential/graph/cycle/loop execution, variables and retries.
- `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-build --no-restore --configuration Release`: 1,261 pre-merge cases passed. After the Create branch merge, the same suite with `--no-restore --configuration Release -m:1` passed all 1,296 cases.
- `npm run build` (ClientApp): passed before and after resolving Create branch integration conflicts. Run frontend and backend builds sequentially because Vite replaces tracked static assets.
- `npm run test:smoke -- --config=/tmp/formicae-template-smoke.config.ts templates.spec.ts` (ClientApp): all three cases passed (27.4 seconds) after merging 0.34.0, including issue-title binding save/reload and actual inline template execution with output/provenance history. Temporary config uses installed Chromium 1243, one worker and the existing dev servers.
- Installed local Playwright MCP: catalog insertion, textarea/input controls and node ports inspected; zero console errors; API requests returned 200; screenshot and network evidence retained under ignored `test-results/`.
- `helm lint deploy/helm/formicae`: passed.

The first .NET runner invocation required reviewed local socket access; restore required reviewed network access. Preparation's concurrent solution build failed, so locked frontend installation and individual builds were used. Browser tests initially exhausted a 30-second scenario budget under concurrent shared builds; placing Add input above the input list and a 90-second scenario budget produced passing checks. A concurrent merged .NET/Vite build failed as Vite replaced files; sequential validation fixes the build order.

Release sequencing: 0.33.0 Create branch, 0.34.0 issue title, 0.34.1 test selection, then 0.35.0 templating. Hold each push until predecessor deployment success is explicitly confirmed. Group navigation 0.35.1 follows templating deployment.

Tests added/removed/edited: 26 / 0 / 0 cases (23 backend cases, 3 browser scenarios); final selector integration counts recorded after its merge.
