# Custom task scope decisions

Independently reviewed and approved with callback/output-race and worker-deadline amendments. This refines the earlier `2026-09-06-1445-task-issues` proposal after delivered Decisions and Personas work.

- Reusable generic agent task; no alias to a built-in kind and no implicit built-in side effects.
- Runner configuration is agent kind plus bounded timeout; scratch directory only, no repository checkout/token, browser or nested-container provisioning.
- Strict string/decimal/boolean inputs; single-pass allowlisted template tokens, no expression language or recursive expansion.
- Immutable catalog snapshot per workflow version and prepared runtime input/prompt snapshot per TaskRun. Retries retain both; loop iterations capture their own values.
- Plain-text outputs persisted with a fixed explicit limit; oversized output fails instead of silently truncating successful data used by decisions.
- Generic task/status enum values are appended. New catalog plus nullable execution snapshot require a generated EF migration; existing definitions and rows stay unchanged.
- Custom tasks allowed sequentially and inside loops; Parallel remains Plan-only. Ordinary dominating Custom outputs may feed Decisions.
- Exact callback correlation replaces latest-kind fallback because multiple Custom nodes reuse one TaskRunKind. Custom streaming callbacks are log-only; runtime result polling exclusively owns final output/status, including during late/concurrent callbacks. Existing built-in streaming remains unchanged.
- Custom tasks have an independent worker deadline for both agent CLIs, without checkpoint/commit behavior, plus RuntimeJobExecutionPolicy(timeout, 0) in both runtime adapters.
- Empty/whitespace final prompts (including optional-input-only templates) fail clearly before launch.
- Environments, capabilities, scripts, tool installs and scoped secrets remain their own issues. No claim of new sandbox enforcement.

The user delegated plan review and implementation decisions. Parent/root and an independent agent should approve or amend this concrete plan before implementation.
