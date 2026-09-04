# Formicae Agent Self-Testing — Shaping Notes

## Scope

Enable Formicae's implementation agents to test changes to Formicae itself. Provide a fast in-worker application loop for routine development and a nested-kind integration tier for container, Kubernetes, persistence, startup, and deployment-sensitive work.

This is developer tooling for agents. It does not add a management-UI feature, database schema, externally accessible preview service, persisted artifact model, or the future environment-management system.

## Decisions

- Use two verification tiers: fast local API/UI testing for every implementation and optional nested kind for infrastructure-sensitive work.
- Fast testing uses development configuration, fake adapters, in-memory persistence, disabled discovery, and no production credentials.
- Use Playwright Test for deterministic Chromium smoke coverage and Playwright MCP for interactive troubleshooting by Codex.
- Browser access remains private to the worker and limited to loopback origins.
- Store transient process state under `/tmp`; store ignored diagnostic artifacts under `test-results/`.
- Model runtime needs as explicit `RuntimeJobSpec` requirements: `BrowserAutomation` and `NestedContainerRuntime`.
- Temporarily grant both requirements to every `Implement` and `AddressComments` job; planning and authentication jobs stay lightweight.
- Use a privileged DinD sidecar with pod-local socket and storage. Never expose the node's container socket, service-account token, host network, or host filesystem.
- Preserve failed nested clusters only when `FORMICAE_E2E_KEEP_CLUSTER=true` is explicitly set.
- Keep verification agent-owned and evidence-based; do not introduce a Formicae-specific post-agent enforcement gate.
- Pin browser and container toolchain versions in the worker image rather than downloading tools per run.
- Use Chromium only initially.
- Treat privileged DinD for all implementation repositories as a transitional security tradeoff until issues #16, #17, #19, #20, #21, and #22 provide capability- and environment-level configuration.
- Make a single minor version bump to `0.7.0` for this backward-compatible feature.

## Context

- **Visuals:** None; this is runtime and development tooling without an operator-facing UI.
- **References:** Existing local development instructions, worker runtime/job generation, prompt templates, container and Kubernetes runtimes, worker image, and Kubernetes E2E harness.
- **Product alignment:** Directly supports the Kubernetes-native, ephemeral-agent MVP by allowing implementation agents to verify their own work. It preserves future extensibility for capabilities, environments, MCP configuration, custom images, and tool installs without implementing those post-launch features now.

## Standards Applied

No current repository standard applies. The only indexed standard concerns Entity Framework migrations, and this feature intentionally adds no database model or migration.
