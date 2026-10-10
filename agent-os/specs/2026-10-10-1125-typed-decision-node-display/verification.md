# Release verification

Release: 0.32.0. Latest base: `14db652` (0.31.0); `git fetch origin` and `git merge origin/main` confirmed no incoming changes.

- `dotnet restore hhnl.Formicae.slnx`: passed.
- `dotnet build hhnl.Formicae.slnx --no-restore`: passed, zero warnings/errors.
- `dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore`: 1,238 passed, none failed/skipped.
- Targeted Decision/Editor/Cycle/TaskDataPassing/Variables filter: 241 passed. Final `--filter FullyQualifiedName~TypedDecision`: 30 passed, including numeric precision validation.
- `npm run build` in ClientApp: passed; existing bundle-size advisory remains.
- `PLAYWRIGHT_BROWSERS_PATH=/tmp/formicae-playwright npm run test:smoke -- --timeout=60000`: 103 passed, five timeouts during concurrent browser activity.
- `PLAYWRIGHT_BROWSERS_PATH=/tmp/formicae-playwright npm run test:smoke -- --last-failed --workers=1 --timeout=90000`: all five passed. All 108 cases passed across the full run and serial retries.
- `PLAYWRIGHT_BROWSERS_PATH=/tmp/formicae-playwright ./scripts/formicae-dev.sh prepare`, followed by `start`, `status`, `logs`, `stop`: passed; API healthy, Vite responding, services stopped.
- `helm lint deploy/helm/formicae`: passed (one chart, zero failures).
- `git diff --check`: passed.

Browser coverage includes boolean route persistence, string/number inference and cases, mandatory Default validation, reload, type replacement and undo, named custom-task insertion, and pinned group colors/membership after execution refresh. Screenshots and failure traces were inspected; the saved group screenshot is in `visuals/management-groups.png`. The configured remote browser could not access the local worker server; local Playwright exercised the running application instead. Browser resources were coordinated through the Paseo skill and released before the other project's acceptance resumed.

Tests added: 34 cases (30 backend, four browser). Removed: zero. Existing browser cases edited: four. Backend additions use seven test declarations.

No migration or Kubernetes/job-runtime settings changed. Automated main-branch checks include Kubernetes E2E and deployment image/health verification.
