# Decisions

- User selected per-step AI configuration and model, CLI-based discovery, and Codex-first coverage.
- User selected workflow-model inheritance even when the step selects another configuration.
- Other agent configurations retain default-model behavior; ACP execution is not implemented by this feature.
- No reasoning-effort selection or automatic model fallback. Unknown saved models remain visible.
- No schema migration expected: definition JSON and existing workflow events carry the changes.
- Existing selected-step editor is the UI reference; no supplied visuals.
