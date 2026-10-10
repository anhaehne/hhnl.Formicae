# Documentation release verification

Release: **0.34.1**, documentation only. Runtime implementation and lifecycle acceptance tests remain planned. Tests added/removed/edited: **0 / 0 / 0**.

## Local validation

`dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --configuration Release --filter 'FullyQualifiedName~EnvironmentDefinitionTests|FullyQualifiedName~RuntimeEnvironmentExtensionTests|FullyQualifiedName~EnvironmentRuntimePolicyTests|FullyQualifiedName~RuntimeLifecycleTests' -m:1`: **105 passed**, 0 failed/skipped. The initial sandboxed run could not bind MSBuild's local IPC pipe; rerunning with required local IPC/network access passed.

`helm lint deploy/helm/formicae`: **passed**, one chart and zero failures; existing optional icon recommendation only. `helm template formicae deploy/helm/formicae --namespace formicae > /tmp/formicae-capability-spec-chart.yaml`: **passed**. `git diff --check`: **passed**.

`dotnet test tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj --no-restore --configuration Release -m:1`: **1,276 passed**, 0 failed/skipped.

An inline Python documentation check verified required spec/contract files, **34 local Markdown links across 10 documents** and aligned props/chart/appVersion/image tag **0.34.1**. The contract traces are documented examples, not claims of executed Kubernetes behavior. Automated release results are reported with the release completion.

No application source, runtime manifest, migration or UI behavior is changed. Local browser/runtime harness and Kubernetes E2E execution are not required for this design-only change; their future implementation requirements are recorded in `plan.md`. Automated release workflows still run the normal repository suite and image/deployment validation.

## Release workflow

Post-save read-only review found two design contract gaps: immutable suspended-Job timeout bindings and cancellation racing uncertain create/unsuspend operations. Local follow-up documentation specifies a fixed absolute worker deadline with remaining-time/checkpoint calculation, plus durable cancellation fencing, confirmed worker termination before companion deletion and uncertain-outcome recovery. Documentation link and whitespace checks cover this follow-up; no runtime code or tests changed. Hold publication of the follow-up for the coordinated release queue.

Reviewed and fast-forwarded the clean work branch from **0.33.0** to latest origin/main **0.34.0** before documentation edits; retained incoming GitHub issue-title behavior and standards communication preference. Refreshed main again before choosing this fresh patch bump. Push must be fast-forward and preserve incoming changes. Verify the pushed documentation commit through Test, Build container images and Deploy Formicae, including deployment image revision/health checks; do not infer deployment from a successful push or chart publication alone.
