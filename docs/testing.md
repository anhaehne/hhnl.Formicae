# Task-based validation

Use the repository runner to explain validation before starting expensive tests:

```bash
node scripts/test-selection.mjs --base origin/main --plan
node scripts/test-selection.mjs --base origin/main --run
```

Node.js 22 and Git are required. The runner uses existing .NET/xUnit and Playwright commands, with no additional packages. Install frontend dependencies with `npm ci` in `src/hhnl.Formicae.Api/ClientApp` and install Chromium when browser execution is needed. `--plan` starts no builds, containers or servers. `--list` performs fast verification and discovers actual tests without executing them; it can restore/build projects and needs installed frontend dependencies. `--run` performs fast verification, discovery and execution. Stage new files before the final whitespace check, since `git diff --check HEAD` does not inspect untracked files.

## Selecting tests

The default base is `origin/main`. Fetch the latest base before final validation. Selection compares the merge base with HEAD, then includes staged/unstaged edits and untracked files. Renames include old and new paths; deleted paths remain inputs. An unavailable base fails rather than silently selecting nothing. Use `--base <ref>` to select another target.

`scripts/test-selection.json` maps changed paths to overlapping families. Specific mappings take precedence over generic fallback mappings. Family filters use test class/name fragments, rather than filenames: for example, `PersonaPersistenceTests`, `EnvironmentPersistenceTests` and `CustomTaskPersistenceTests` live in catalog files. All persistence classes and migrations belong to `persistence`. Shared workflow code selects workflow regression and durable contracts; shared editor/graph code selects full browser regression. Build/dependency/runtime/deployment changes select broad validation, including Kubernetes. Unmapped inputs and an empty automatic selection select full validation.

The runner prints every selection reason and the actual discovered tests before execution. It rejects missing specs, empty selected families, failed commands and unrecognized discovery output. Browser families include the tagged smoke checks in one server lifecycle. Families are conservative impact mappings, not coverage-derived dependency analysis. Review consumers when adding a feature; add mappings and selector regression tests together. A mapping must not turn an unknown production input into a docs-only result.

For quick iteration, explicitly choose a family:

```bash
node scripts/test-selection.mjs --family personas --run
node scripts/test-selection.mjs --base origin/main --family integrations --run
```

`--family` alone runs focused validation; it does not assess all changed files. With `--base`, explicit families are added to automatic selection. Repeat `--family` to include more consumers. `--file PATH` supplies hypothetical paths for reviewing mappings; it replaces Git discovery and must not be used as final change-impact validation. List family names with `--help`.

For a single regression, native selectors remain useful:

```bash
dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --filter 'FullyQualifiedName~PersonaPromptComposerTests'
# From src/hhnl.Formicae.Api/ClientApp:
npm run test:browser -- tests/e2e/personas.spec.ts
```

Generic prose can use documentation checks. Prompts, executable examples, worker-consumed documents and fixture data need their consumer families; operational instructions need deployment checks. The manifest cannot inspect the meaning of prose: add explicit families for executable or ambiguous changes. Mixed changes take the union. Policy/selector changes run the selector regression suite.

## Fast verification and full validation

Fast verification is `git diff --check HEAD`, a Release backend test-project build for backend families, and `npm run build` for browser families. Selector changes also run:

```bash
node --test scripts/tests/test-selection.test.mjs
```

The runner builds current sources before using `--no-build --no-restore`, preventing stale binaries from being tested. Preparation is separate from repetition: run the existing development harness `prepare` once, and refresh dependencies when lockfiles change. Stop development servers before browser suites because Playwright owns ports 5000/5173 and does not reuse existing servers. Coordinate shared resources with other agents; this runner does not acquire cross-agent locks.

`npm run test:smoke` runs three tagged tests: API health/version, primary navigation and UI page/console errors. `npm run test:browser` runs all browser tests, including smoke. Tags change selection only, not test assertions or isolation. CI retains full backend, browser, worker-image, Helm and Kubernetes validation on the final merged main commit. Full applicable suites may be satisfied by verified CI on the exact final merged SHA rather than duplicated locally after targeted regressions and fast verification. Keep the live browser inspection requirement for runtime/UI behavior changes. Release/version metadata changes use the same final full CI; do not repeatedly run unrelated local suites solely for a metadata bump.

Deployment families execute Helm lint and the existing Kubernetes E2E runner. Keep the existing cleanup policy; preserve clusters only during active troubleshooting. Selecting fewer Kubernetes tests still incurs image/cluster setup, so the first version keeps the full Kubernetes suite.

## Measuring later improvements

The runner prints elapsed setup, build, discovery and execution times separately. Record cold and warm runs on the same resources before comparing changes. First measure PostgreSQL container startup across the ten class fixtures: collection sharing reduces startup but serializes classes in xUnit 2.9.3. Next evaluate reusing a verified worker image per CI SHA and splitting independent backend/browser jobs. Fixture isolation, build artifact correctness and final-commit CI coverage remain required. This release does not pool containers, change concurrency, reuse live servers or claim measured speedups.

## Initial verification evidence

The initial 0.34.2 tooling check passed `node --test scripts/tests/test-selection.test.mjs` (21 tests), `node scripts/test-selection.mjs --family selector --run`, `dotnet build tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --configuration Release`, `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-build --no-restore --configuration Release --filter 'FullyQualifiedName~PersonaPromptComposerTests'` (4 tests), and `npm run build`. Native `dotnet test ... --list-tests` resolved 1,238 cases on the original 0.32.1 base; native `playwright test --list --reporter=json` resolved all 108 browser cases and every family. `node scripts/test-selection.mjs --family smoke --list` built the UI, resolved exactly three tagged smoke cases and verified native test-list identity round-tripping without starting servers. These are discovery counts, not claims that the full suites were executed locally. Final merged-commit counts come from CI. Tests added/removed/edited: **22 / 0 / 3**; the three edits add smoke tags without changing assertions.

After intentionally merging 0.33.0 and 0.34.0 and resolving prepend-only release/baseline conflicts by retaining both sides, `node scripts/test-selection.mjs --family backend --family browser --list` passed both builds and exact native browser selection, discovering **1,276 backend / 111 browser** cases. Every manifest family token/spec resolved against the merged native reports, including Create branch consumers. The 22 selector tests, four targeted backend cases, `git diff --check HEAD` and `helm lint deploy/helm/formicae` passed again. The incoming instruction to read applicable standards directly is preserved.

Final impact review expanded workflow selection to dedicated CustomTask, Environment, Persona and ScriptTask orchestration/preparation consumers, and made shared API/model/runtime interfaces select broad cross-layer checks. The resulting **22 selector regressions** pass and all added patterns resolve against the merged native discovery reports.
