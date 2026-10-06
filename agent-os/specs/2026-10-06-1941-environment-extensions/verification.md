# Verification and acceptance evidence

All six feature acceptance paths are verified: #12 scripts, #16 capabilities, #18 selected secrets, #20 native MCP, #21 images and #22 tool installs. Version 0.20.0 includes one generated nullable task/attempt exit-code migration. Legacy workflow history remains intact.

## Commands and results

| Command | Result |
| --- | --- |
| `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore -m:1` | 978/978 passed: orchestration, controls, provenance, environment validation, migrations and real worker subprocesses |
| `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore -m:1 --filter 'FullyQualifiedName~WorkerEnvironmentExtensionTests\|FullyQualifiedName~RuntimeEnvironmentExtensionTests\|FullyQualifiedName~CodexWorkspaceTests'` | 39/39 passed after the portable test-workspace correction; no build warnings |
| `npm run build` in `src/hhnl.Formicae.Api/ClientApp` | Passed; tracked production assets refreshed; existing large-chunk advisory remains |
| `PLAYWRIGHT_BROWSERS_PATH=/workspace/hhnl.Formicae/.artifacts/playwright-browsers npm run test:smoke -- --config /workspace/hhnl.Formicae/.artifacts/environment-extensions/playwright.config.ts extensions.spec.ts --workers=1` | 6/6 passed; all new UI feature paths covered |
| `PLAYWRIGHT_BROWSERS_PATH=/workspace/hhnl.Formicae/.artifacts/playwright-browsers npm run test:smoke -- --config /workspace/hhnl.Formicae/.artifacts/environment-extensions/playwright.config.ts stepEnvironments.spec.ts --grep 'task cards show' --repeat-each=3 --workers=1` | 3/3 passed unchanged after isolating browser verification from Docker network changes |
| `docker build -f src/hhnl.Formicae.Worker/Dockerfile -t formicae-worker:environment-extensions-test .` | Passed, including shared typed application models and Unicode-safe native configuration |
| `FORMICAE_E2E_WORKER_IMAGE=formicae-worker:environment-extensions-test ./scripts/run-k8s-e2e.sh` | 13/13 passed in an isolated kind cluster; real scripts, selected keys, preserved operator Secrets, compatible custom image, ordered bootstrap, capability filtering and both harness preparation paths |
| `FORMICAE_E2E_WORKER_IMAGE=formicae-worker:environment-extensions-test dotnet test tests/hhnl.Formicae.KubernetesE2ETests/hhnl.Formicae.KubernetesE2ETests.csproj --no-restore -m:1 --filter FullyQualifiedName~Native_worker_materializes_only_enabled_mcp_servers_in_codex_and_openhands_formats` | 1/1 passed after the Unicode correction: real worker Jobs, native Python TOML/JSON parsing, exact emoji-containing arguments and selected secret values |
| `helm lint deploy/helm/formicae` and `git diff --check` | Passed |

The full 75-test browser command is `PLAYWRIGHT_BROWSERS_PATH=/workspace/hhnl.Formicae/.artifacts/playwright-browsers npm run test:smoke -- --workers=1`. It and all GitHub required checks are release gates. [PR #69](https://github.com/anhaehne/hhnl.Formicae/pull/69) records their final results and deployment outcome. No checks are waived.

## Investigation and resolved findings

GPT-6 Astra reviewed contracts, native Codex/OpenHands integration and all six acceptance criteria. Findings were implemented: task-owned native MCP files, whole-project Codex trust override on initial/resumed execution, HTTP alias restrictions, native TOML Unicode handling and UI transport/argument/nullability safeguards.

Native Playwright MCP inspected saved environment image/tool/MCP references, a Script node with connected scalar output, secret-reference controls and alternating historical task selection. Console, page-error and network inspection were clean. Evidence lives under `.artifacts/environment-extensions/browser/` and `.artifacts/environment-extensions/regression-browser/`, including screenshots, snapshots, traces, console/network captures and inspection JSON.

Initial integration checks identified legacy snapshot comparisons including the newly added nullable column and assertions comparing JSON-escaped logs; those expectations were corrected while retaining old-field evidence. Hosted CI exposed worker tests assuming a writable `/workspace`; internal test invocation now accepts a temporary directory while production retains its normal workspace. One browser navigation failed with Chrome `ERR_NETWORK_CHANGED` before React mounted; the unchanged test then passed three repeats and native MCP investigation. Final browser verification is isolated from local Docker/kind network changes.

Tests added: 44 declarations / 99 cases across backend, browser and Kubernetes, plus one case added to an existing classification theory. Tests edited: 6 existing declarations and shared fixture/audit helpers. Tests removed: 0.
