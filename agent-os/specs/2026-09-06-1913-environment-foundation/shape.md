# Environment foundation scope decisions

Planning only; independently approved after deadline/audit amendments, with shared interfaces in [contracts.md](contracts.md). #19 is the environment family's reusable catalog/configuration foundation, selected at workflow scope. #17 owns per-step overrides, #21 images, #22 tools, #18 secrets, #20 MCP and #16 enforceable capabilities.

- Immutable default plus revisioned, soft-deleted catalogs reuse existing Persona/Custom Task patterns.
- First usable setting is an optional hard-timeout cap; no resource/credential/permission expansion.
- Image/tools/MCP have guarded schema extension positions. Nonempty unsupported values fail clearly, and the UI does not present nonfunctional configuration controls.
- Pin catalog configuration in workflow JSON; deployment-global defaults remain platform controls. History records selected profile constraints only, never recomputed settings masquerading as observed facts for an existing job.
- Capped jobs with zero effective checkpoint grace use an independent hard worker deadline, including one-second Implement/AddressComments caps. No-cap behavior remains unchanged.
- Capped OpenHands always uses the hard deadline because it has no worker checkpoint path, including positive runtime grace; no automatic commits are added. Positive-grace Codex retains checkpoint behavior, while zero-grace Codex retains ordinary successful commits within the hard deadline.
- One shared preparation/resolution path serves sequential and Parallel AI tasks and will later support #17 overrides.
- Generated migration adds the catalog; no legacy definition rewrite, no speculative execution table, no manual infrastructure mutation.

The scope fulfills #19 acceptance criteria without pretending that sibling environment features are already implemented. Closing documentation must retain that distinction.
